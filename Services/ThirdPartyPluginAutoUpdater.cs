// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using System.Threading;

namespace RustArchon.Api.Services;

/// <summary>Applies newer versions of third-party plugins on the servers that asked for it, when the three gates allow.</summary>
public interface IThirdPartyPluginAutoUpdater
{
    /// <summary>One pass. Returns how many updates it started.</summary>
    Task<int> RunPassAsync(DateTimeOffset now);
}

/// <inheritdoc cref="IThirdPartyPluginAutoUpdater" />
/// <remarks>
/// <para>
/// <b>It only presses the buttons.</b> Everything an update is checked against is decided by <see cref="IThirdPartyPluginUpdateService"/>, the same code a
/// person's click goes through; this decides only <i>when</i> and <i>which first</i>. It considers a server only if it has opted in, and starts an update
/// only while the plan offers the feature and the server is outside its days-before-wipe window (both checked by the service). The site-wide switch
/// (<see cref="PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled"/>) stops every new start at once; updates already under way are still followed
/// to their outcome, so a rollback is never left unrecorded.
/// </para>
/// <para>
/// One update at a time per server (a swap and its reload are watched before another begins), at most <see cref="MaxStartsPerPass"/> in a pass so
/// nothing hits every server in the same moment. An update that did not work out is not tried again on that server until the author publishes another
/// version; a server that could not be reached is simply tried again on the next pass.
/// </para>
/// </remarks>
public class ThirdPartyPluginAutoUpdater(
    ApiDbContext context,
    IThirdPartyPluginUpdateService service,
    IPlatformSettingsCache settings,
    ILogger<ThirdPartyPluginAutoUpdater> logger) : IThirdPartyPluginAutoUpdater
{
    /// <summary>Most updates started in one pass.</summary>
    public const int MaxStartsPerPass = 5;

    public async Task<int> RunPassAsync(DateTimeOffset now)
    {
        var enabled = await settings.GetBooleanAsync(PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled, defaultValue: true);

        // Servers that opted in, and any with an update still waiting for its outcome (a person may have started one, and may since have opted out).
        var pendingServerIds = await context.ThirdPartyPluginUpdates.AcrossAllTenants().AsNoTracking()
            .Where(u => u.State == ThirdPartyPluginUpdateStates.Started).Select(u => u.RustServerId).Distinct().ToListAsync();
        var servers = await context.RustServers.AcrossAllTenants().AsNoTracking()
            .Where(s => s.IsEnabled && (s.ThirdPartyPluginUpdatesEnabled || pendingServerIds.Contains(s.Id)))
            .OrderBy(s => s.Id)
            .ToListAsync();

        var started = 0;
        foreach (var server in servers)
        {
            try
            {
                await service.ReconcileAsync(server, now);

                if (!enabled || !server.ThirdPartyPluginUpdatesEnabled || started >= MaxStartsPerPass)
                {
                    continue;
                }

                if (await StartOneAsync(server, now))
                {
                    started++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The automatic third-party plugin update failed for server {ServerId}.", server.Id);
            }
        }

        return started;
    }

    // The first plugin (by name) whose update the service will start; the rest wait for the next pass, since only one is done at a time.
    private async Task<bool> StartOneAsync(RustServer server, DateTimeOffset now)
    {
        var offers = await service.GetOffersAsync(server, now);
        foreach (var (normalizedName, offer) in offers.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            if (offer.State != ThirdPartyUpdateOfferStates.Ready || !offer.CanApply)
            {
                continue;
            }

            var name = await context.PluginUpdateNotices.AcrossAllTenants().AsNoTracking()
                .Where(n => n.RustServerId == server.Id && n.NormalizedName == normalizedName).Select(n => n.Name).FirstOrDefaultAsync();
            if (name is null)
            {
                continue;
            }

            var result = await service.StartAsync(server, name, PluginUpdateTriggers.Auto, offer.FileSha256);
            if (result.Started)
            {
                logger.LogInformation("Started the automatic update of {Plugin} on server {ServerId}.", name, server.Id);
                return true;
            }

            // Refusals the service makes itself (held for the wipe, already tried, and so on) are expected and checked again next pass.
            logger.LogDebug("The automatic update of {Plugin} on server {ServerId} was not started: {Code}.", name, server.Id, result.Code);
        }

        return false;
    }
}

/// <summary>Runs <see cref="IThirdPartyPluginAutoUpdater"/> every few minutes. A failed pass is logged and tried again next time.</summary>
public class ThirdPartyPluginAutoUpdateService(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<ThirdPartyPluginAutoUpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(4);

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
                await scope.ServiceProvider.GetRequiredService<IThirdPartyPluginAutoUpdater>().RunPassAsync(clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automatic third-party plugin update pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
