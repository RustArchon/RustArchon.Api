// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Services;

/// <summary>
/// The one shared "leave the marketplace index alone until" for the whole Api process, so that being told to slow down stops every
/// lookup, not just the one that heard it.
/// </summary>
public sealed class PluginDownloadThrottle
{
    private long _pausedUntilTicks;

    public DateTimeOffset PausedUntil => new(Interlocked.Read(ref _pausedUntilTicks), TimeSpan.Zero);

    /// <summary>Pauses until <paramref name="until"/>; never shortens a pause already in force.</summary>
    public void PauseUntil(DateTimeOffset until)
    {
        var ticks = until.UtcTicks;
        long seen;
        while (ticks > (seen = Interlocked.Read(ref _pausedUntilTicks)) && Interlocked.CompareExchange(ref _pausedUntilTicks, ticks, seen) != seen)
        {
        }
    }
}

/// <summary>Finds the direct download address of each plugin UpdateChecker says has an update, once per plugin version for the whole platform.</summary>
public interface IPluginDownloadLookupJob
{
    /// <summary>One pass. Returns how many plugins were asked about.</summary>
    Task<int> RunOnceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPluginDownloadLookupJob" />
/// <remarks>
/// <para>
/// <b>Why this is a job and not a lookup when a page is read:</b> a hundred servers running the same plugin need one answer between them,
/// and a page load must never wait on, or be able to hammer, a third party. So the answer is asked for here, stored (whole - see
/// <see cref="PluginDownloadLookup"/>), and only ever read back by pages.
/// </para>
/// <para>
/// <b>Being a good guest:</b> at most <see cref="DefaultMaxPerRun"/> plugins per pass, a pause between asks, one ask per plugin version ever
/// once it is found, a longer wait each time an ask has not worked (<see cref="NextAttempt"/>), and a stop for everyone - honouring what
/// the index asks for - the moment it says it is being asked too often.
/// </para>
/// </remarks>
public class PluginDownloadLookupJob(
    IPluginDownloadLookupRepository lookups,
    IPluginDownloadResolver resolver,
    IPlatformSettingsCache settings,
    PluginDownloadThrottle throttle,
    TimeProvider clock,
    ILogger<PluginDownloadLookupJob> logger) : IPluginDownloadLookupJob
{
    /// <summary>The most plugins asked about in one pass.</summary>
    public const int DefaultMaxPerRun = 20;

    public int MaxPerRun { get; init; } = DefaultMaxPerRun;

    /// <summary>The pause between two asks in one pass. Settable so tests do not have to wait for it.</summary>
    public TimeSpan Spacing { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await settings.GetBooleanAsync(PlatformSettingsRegistry.PluginDownloadLookupEnabled, defaultValue: true))
        {
            return 0;
        }

        if (throttle.PausedUntil > clock.GetUtcNow())
        {
            return 0;
        }

        var due = await lookups.GetDueAsync(clock.GetUtcNow(), MaxPerRun);
        var asked = 0;
        foreach (var request in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asked > 0 && Spacing > TimeSpan.Zero)
            {
                await Task.Delay(Spacing, cancellationToken);
            }

            var answer = await resolver.AskAsync(request, cancellationToken);
            var now = clock.GetUtcNow();
            var previous = await lookups.FindAsync(request.MarketplaceKey, request.NormalizedName, request.VersionKey);
            await lookups.SaveAsync(Build(request, answer, previous?.Attempts ?? 0, now));
            asked++;

            if (answer.AskedToWait is { } wait)
            {
                throttle.PauseUntil(now + wait);
                logger.LogWarning("Plugin download lookups paused until {Until:o}.", now + wait);
                break;
            }
        }

        return asked;
    }

    /// <summary>The row to store for an answer: what was asked, what came back, what was made of it, and when to try again if it did not settle.</summary>
    public static PluginDownloadLookup Build(PluginDownloadRequest request, PluginDownloadAnswer answer, int previousAttempts, DateTimeOffset now)
    {
        var attempts = previousAttempts + 1;
        var outcome = answer.Match.Outcome;
        return new PluginDownloadLookup
        {
            MarketplaceKey = request.MarketplaceKey,
            NormalizedName = request.NormalizedName,
            Version = request.VersionKey,
            Outcome = outcome,
            DownloadUrl = outcome == PluginDownloadOutcome.Found ? answer.Match.DownloadUrl : null,
            MatchedName = answer.Match.MatchedName,
            MatchedPageUrl = answer.Match.MatchedPageUrl,
            MatchedVersion = answer.Match.MatchedVersion,
            Reason = answer.Match.Reason,
            ResponseJson = answer.ResponseJson,
            ResponseTruncated = answer.ResponseTruncated,
            HttpStatus = answer.HttpStatus,
            CheckedAtUtc = now,
            Attempts = attempts,
            NextAttemptUtc = outcome == PluginDownloadOutcome.Found ? null : now + NextAttempt(outcome, attempts)
        };
    }

    /// <summary>
    /// How long to wait before asking again after an answer that did not settle: a listing that is not there yet is worth another look
    /// tomorrow-ish and then weekly; a failure is retried sooner but backs off, so a down index is not hammered.
    /// </summary>
    public static TimeSpan NextAttempt(PluginDownloadOutcome outcome, int attempts)
    {
        var step = Math.Min(Math.Max(attempts, 1) - 1, 10);   // 2^10 is already far past both ceilings
        return outcome == PluginDownloadOutcome.NotFound
            ? Min(TimeSpan.FromHours(12 * Math.Pow(2, step)), TimeSpan.FromDays(7))
            : Min(TimeSpan.FromMinutes(15 * Math.Pow(2, step)), TimeSpan.FromHours(6));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>Runs <see cref="IPluginDownloadLookupJob"/> every few minutes. A failed pass is logged and tried again next time.</summary>
public class PluginDownloadLookupService(IServiceScopeFactory scopeFactory, ILogger<PluginDownloadLookupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IPluginDownloadLookupJob>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Plugin download lookup pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
