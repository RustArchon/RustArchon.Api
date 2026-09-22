// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Services;

/// <summary>Looks at the file behind each found download address that somebody is waiting on, once per file.</summary>
public interface IPluginFileValidationJob
{
    /// <summary>One pass over what is due. Returns how many files were downloaded.</summary>
    Task<int> RunOnceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the file for this lookup again, whatever state it is in. If it is byte-for-byte the file already looked at, the checks are not
    /// repeated; if it is not, the author has replaced it under the same version number, so it is looked at again and the change is recorded.
    /// Returns the updated row, or <c>null</c> if there is no such row or it has no address.
    /// </summary>
    Task<PluginDownloadLookup?> RecheckAsync(Guid lookupId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPluginFileValidationJob" />
/// <remarks>
/// <para>
/// <b>What is kept:</b> what was learned about the file - its SHA-256, what kind of file it is, whether it can be applied and why - and never
/// the file. RustArchon may not have the licence to redistribute these plugins; the plugin on each game server downloads its own copy and is held
/// to the hash recorded here (Scott, 2026-09-21).
/// </para>
/// <para>
/// <b>Once per file, unless it changes:</b> a settled file is not downloaded again by the job. A deliberate re-check (see
/// <see cref="RecheckAsync"/>) downloads it and compares the hash first: the same hash means the earlier answer stands and the checks are skipped;
/// a different one means the file was replaced under the same version number - bad practice, but possible - so it is looked at afresh.
/// </para>
/// <para>
/// <b>Being a good guest:</b> only files somebody is waiting on (see <see cref="IPluginDownloadLookupRepository.GetDueForValidationAsync"/>), a few
/// per pass with a pause between them, a longer wait each time a download has failed, and a host's own "too often" honoured.
/// </para>
/// </remarks>
public class PluginFileValidationJob(
    IPluginDownloadLookupRepository lookups,
    IPluginFileDownloader downloader,
    IPluginFileInspector inspector,
    IPlatformSettingsCache settings,
    TimeProvider clock,
    ILogger<PluginFileValidationJob> logger) : IPluginFileValidationJob
{
    /// <summary>The most files downloaded in one pass.</summary>
    public const int DefaultMaxPerRun = 10;

    public int MaxPerRun { get; init; } = DefaultMaxPerRun;

    /// <summary>The pause between two downloads in one pass. Settable so tests do not have to wait for it.</summary>
    public TimeSpan Spacing { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await settings.GetBooleanAsync(PlatformSettingsRegistry.PluginFileValidationEnabled, defaultValue: true))
        {
            return 0;
        }

        var due = await lookups.GetDueForValidationAsync(clock.GetUtcNow(), MaxPerRun);
        var downloaded = 0;
        foreach (var row in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (downloaded > 0 && Spacing > TimeSpan.Zero)
            {
                await Task.Delay(Spacing, cancellationToken);
            }

            await ValidateAsync(row, recheck: false, cancellationToken);
            downloaded++;
        }

        return downloaded;
    }

    public async Task<PluginDownloadLookup?> RecheckAsync(Guid lookupId, CancellationToken cancellationToken)
    {
        var row = await lookups.FindByIdAsync(lookupId);
        if (row is null || row.Outcome != PluginDownloadOutcome.Found || row.DownloadUrl is null)
        {
            return null;
        }

        return await ValidateAsync(row, recheck: true, cancellationToken);
    }

    private async Task<PluginDownloadLookup> ValidateAsync(PluginDownloadLookup row, bool recheck, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        row.ValidationAttempts++;

        // Judged by today's rules, not those of the day it was stored: tightening the allowed addresses takes effect on what is already held.
        var address = PluginDownloadMatcher.SafeDownloadUrl(row.DownloadUrl, row.MarketplaceKey);
        if (address is null)
        {
            return await FailAsync(row, "the stored address is no longer one that would be fetched", null, now);
        }

        var download = await downloader.DownloadAsync(new Uri(address), cancellationToken);
        if (download.TooLarge)
        {
            // Not a failure to try again: the file is what it is. Recorded as not applicable, and it replaces an earlier answer too - a file that has
            // grown past the bound is not the file that was checked.
            row.ValidationState = PluginFileValidationState.Invalid;
            row.ValidationReason = Limit(download.Reason, 300);
            row.ValidatedAtUtc = now;
            row.ValidationNextAttemptUtc = null;
            await lookups.SaveValidationAsync(row);
            return row;
        }

        if (!download.Ok)
        {
            if (recheck && IsSettled(row))
            {
                // A download that did not work is no evidence the file changed: what was worked out about it stands.
                logger.LogInformation("Re-checking the file for plugin {Plugin} {Version} failed ({Reason}); the earlier answer stands.", row.NormalizedName, row.Version, download.Reason);
                return row;
            }

            return await FailAsync(row, download.Reason, download.RetryAfter, now);
        }

        var content = download.Content!;
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        if (recheck && IsSettled(row) && string.Equals(row.FileSha256, sha, StringComparison.Ordinal))
        {
            // The file it was: the earlier answer stands, and is not worked out again.
            row.ValidatedAtUtc = now;
            await lookups.SaveValidationAsync(row);
            return row;
        }

        var found = inspector.Inspect(content, row.NormalizedName, row.Version);

        if (row.FileSha256 is not null && !string.Equals(row.FileSha256, found.Sha256, StringComparison.Ordinal))
        {
            row.PreviousFileSha256 = row.FileSha256;
            row.FileHashChanges++;
            logger.LogWarning(
                "The file for plugin {Plugin} {Version} on {Marketplace} changed under the same version number ({Old} -> {New}).",
                row.NormalizedName, row.Version, row.MarketplaceKey, row.FileSha256, found.Sha256);
        }

        row.ValidationState = found.State;
        row.ValidationReason = found.Reason;
        row.FileKind = found.Kind;
        row.FileSha256 = found.Sha256;
        row.FileSizeBytes = found.SizeBytes;
        row.PluginClassName = Limit(found.ClassName, 200);
        row.PluginInfoName = Limit(found.InfoName, 200);
        row.PluginInfoAuthor = Limit(found.InfoAuthor, 200);
        row.PluginInfoVersion = Limit(found.InfoVersion, 50);
        row.ZipEntries = found.ZipEntries is null ? null : ZipListing.Format(found.ZipEntries);
        row.ZipSourceFindings = found.ZipSourceFindings is null ? null : ZipListing.FormatFindings(found.ZipSourceFindings);
        row.ValidatedAtUtc = now;
        row.ValidationNextAttemptUtc = null;
        await lookups.SaveValidationAsync(row);
        return row;
    }

    private async Task<PluginDownloadLookup> FailAsync(PluginDownloadLookup row, string reason, TimeSpan? retryAfter, DateTimeOffset now)
    {
        var wait = PluginDownloadLookupJob.NextAttempt(PluginDownloadOutcome.Failed, row.ValidationAttempts);
        if (retryAfter is { } asked && asked > wait)
        {
            wait = asked;
        }

        row.ValidationState = PluginFileValidationState.Failed;
        row.ValidationReason = Limit(reason, 300);
        row.ValidationNextAttemptUtc = now + wait;
        await lookups.SaveValidationAsync(row);
        return row;
    }

    // Worked out already (whatever the answer was), as opposed to not looked at or not fetchable yet.
    private static bool IsSettled(PluginDownloadLookup row) =>
        row.ValidationState is PluginFileValidationState.Valid or PluginFileValidationState.Invalid or PluginFileValidationState.NeedsInstructions;

    private static string? Limit(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}

/// <summary>Runs <see cref="IPluginFileValidationJob"/> every few minutes. A failed pass is logged and tried again next time.</summary>
public class PluginFileValidationService(IServiceScopeFactory scopeFactory, ILogger<PluginFileValidationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

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
                await scope.ServiceProvider.GetRequiredService<IPluginFileValidationJob>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Plugin file validation pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
