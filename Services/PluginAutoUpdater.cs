// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Services;

/// <summary>Brings the plugin and the Updater on servers that asked for it up to the versions this Panel serves.</summary>
public interface IPluginAutoUpdater
{
    /// <summary>One pass over every server that has automatic updating on. Returns how many updates it started.</summary>
    Task<int> RunPassAsync(DateTimeOffset now);
}

/// <inheritdoc cref="IPluginAutoUpdater" />
/// <remarks>
/// <para>
/// <b>It only presses the buttons.</b> Everything an update is checked against (the server's key, the signature, the version, the token) is
/// decided by <see cref="IPluginUpdateService"/>, the same code a person's click goes through; this decides only <i>when</i> and <i>which
/// first</i>. It runs for servers whose administrator turned on both "Allow updates" and "Update automatically", while the site-wide switch
/// (<see cref="PlatformSettingsRegistry.PluginAutoUpdatesEnabled"/>) is on, and to the version being served: withdrawing a release, or leaving it a
/// draft, is what holds a version back.
/// </para>
/// <para>
/// <b>Order.</b> A server on the current key gets its Updater first (when its plugin can do that) and then its plugin. A server still on an
/// older key gets the plugin first: that update is the bridge to the current key, after which the Updater can follow on the next pass. One
/// update at a time per server; the next waits until the last one's outcome is known (the version changed, or ten minutes passed).
/// </para>
/// <para>
/// <b>No loops.</b> An update that was refused, or started and did not take (the new version failed to come up and was put back), is not tried again
/// on that server until a different version is served. A server that could not be reached is simply tried again on the next pass. Players being
/// online is not a reason to wait: a plugin reload is brief and the Worker collects what the plugin holds every 30 seconds.
/// </para>
/// <para>
/// <b>Staged roll-out.</b> A newly served version is not offered to every server at once when the Platform Setting
/// <see cref="PlatformSettingsRegistry.PluginRolloutHours"/> says so: it becomes eligible on a growing share of them over those hours (see
/// <see cref="IPluginRollout"/>). Only this automatic path is paced; a person's click is not.
/// </para>
/// </remarks>
public class PluginAutoUpdater(
    ApiDbContext context,
    IServerPluginStatusRepository statuses,
    IServerPluginRepository plugins,
    IPluginScriptService script,
    IPluginUpdateAttemptRepository attempts,
    IPluginUpdateService updates,
    IPlatformSettingsCache settings,
    IPluginRollout rollout,
    ILogger<PluginAutoUpdater> logger) : IPluginAutoUpdater
{
    /// <summary>A server whose plugin has not been heard from for this long is not acted on: it is not there to update.</summary>
    public static readonly TimeSpan StatusFreshFor = TimeSpan.FromMinutes(15);

    /// <summary>How long an update that was started may go without the version changing before it counts as failed.</summary>
    public static readonly TimeSpan OutcomeWithin = TimeSpan.FromMinutes(10);

    /// <summary>Most updates started in one pass, so a release does not hit every server at the same moment.</summary>
    public const int MaxStartsPerPass = 5;

    public async Task<int> RunPassAsync(DateTimeOffset now)
    {
        if (!await settings.GetBooleanAsync(PlatformSettingsRegistry.PluginAutoUpdatesEnabled, defaultValue: true))
        {
            return 0;
        }

        var servers = await context.RustServers.AcrossAllTenants().AsNoTracking()
            .Where(s => s.IsEnabled && s.PluginUpdatesEnabled && s.PluginAutoUpdateEnabled)
            .OrderBy(s => s.Id)
            .ToListAsync();
        if (servers.Count == 0)
        {
            return 0;
        }

        var latestMain = await script.GetLatestVersionAsync();
        var latestUpdater = await script.GetLatestUpdaterVersionAsync();
        var mainRollout = latestMain is null ? null : await rollout.BeginAsync(PluginReleaseKind.Main, latestMain, now);
        var updaterRollout = latestUpdater is null ? null : await rollout.BeginAsync(PluginReleaseKind.Updater, latestUpdater, now);

        var started = 0;
        foreach (var server in servers)
        {
            if (started >= MaxStartsPerPass)
            {
                break;
            }

            try
            {
                if (await UpdateOneAsync(server, latestMain, latestUpdater, mainRollout, updaterRollout, now))
                {
                    started++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The automatic plugin update failed for server {ServerId}.", server.Id);
            }
        }

        return started;
    }

    private async Task<bool> UpdateOneAsync(
        RustServer server, string? latestMain, string? latestUpdater, PluginRolloutStatus? mainRollout, PluginRolloutStatus? updaterRollout, DateTimeOffset now)
    {
        var status = await statuses.GetForServerAcrossTenantsAsync(server.TenantId, server.Id);
        if (status is null || now - status.CapturedAtUtc > StatusFreshFor)
        {
            return false;
        }

        var installed = await plugins.GetForServerAcrossTenantsAsync(server.TenantId, server.Id) ?? [];
        var updater = installed.FirstOrDefault(p => string.Equals(p.Name, RustArchonPlugin.UpdaterName, StringComparison.OrdinalIgnoreCase));
        var updaterVersion = updater?.Version?.TrimStart('v', 'V');

        // What earlier updates came to: settle the ones whose outcome is now known, and wait for any that is still on its way.
        var waiting = false;
        foreach (var pending in await attempts.GetPendingAsync(server.Id))
        {
            var current = pending.Kind == PluginUpdateKinds.Updater ? updaterVersion : status.PluginVersion;
            if (PluginVersions.IsAtLeast(current, pending.ToVersion))
            {
                await attempts.ResolveAsync(pending.Id, PluginUpdateAttemptStates.Succeeded, string.Empty, now);
            }
            else if (now - pending.StartedAtUtc > OutcomeWithin)
            {
                await attempts.ResolveAsync(pending.Id, PluginUpdateAttemptStates.Failed, "not_installed", now);
                logger.LogWarning(
                    "The {Kind} update to {To} on server {ServerId} did not take effect; it will not be tried again until another version is served.",
                    pending.Kind, pending.ToVersion, server.Id);
            }
            else
            {
                waiting = true;
            }
        }

        if (waiting)
        {
            return false;
        }

        var mainNeeded = PluginVersions.IsNewer(latestMain, status.PluginVersion);
        var updaterNeeded = latestUpdater is not null && (updater is null || PluginVersions.IsNewer(latestUpdater, updaterVersion));

        var keyState = status.SigningState == PluginSigningStates.Valid
            ? await script.GetKeyStateAsync(status.SigningKeyFingerprint)
            : null;
        var capable = status.Capabilities.Contains(RustArchonPlugin.UpdaterUpdateCapability, StringComparer.Ordinal);

        // A version that is not yet this server's turn in its roll-out waits for a later pass; nothing else about it changes.
        var updaterTurn = updaterRollout is null || PluginRolloutService.Includes(server.Id, PluginReleaseKind.Updater, latestUpdater!, updaterRollout.Fraction);
        var mainTurn = mainRollout is null || PluginRolloutService.Includes(server.Id, PluginReleaseKind.Main, latestMain!, mainRollout.Fraction);
        if ((updaterNeeded && !updaterTurn) || (mainNeeded && !mainTurn))
        {
            logger.LogDebug("Server {ServerId} is not yet due its turn in the staged roll-out.", server.Id);
        }

        if (keyState == PluginKeyState.Active && capable && updaterNeeded && updaterTurn
            && !await attempts.HasUnsuccessfulAsync(server.Id, PluginUpdateKinds.Updater, latestUpdater!))
        {
            return await StartAsync(server, PluginUpdateKinds.Updater, latestUpdater!, () => updates.StartUpdaterAsync(server, PluginUpdateTriggers.Auto));
        }

        if (mainNeeded && mainTurn && !await attempts.HasUnsuccessfulAsync(server.Id, PluginUpdateKinds.Main, latestMain!))
        {
            return await StartAsync(server, PluginUpdateKinds.Main, latestMain!, () => updates.StartAsync(server, PluginUpdateTriggers.Auto));
        }

        return false;
    }

    private async Task<bool> StartAsync(RustServer server, string kind, string toVersion, Func<Task<PluginUpdateResultDto>> start)
    {
        var result = await start();
        if (result.Started)
        {
            logger.LogInformation(
                "Started the automatic {Kind} update to {To} on server {ServerId}.", kind, toVersion, server.Id);
            return true;
        }

        // Refusals the Panel itself makes (not signed by this Panel, and so on) are expected for some servers and are checked again next pass.
        logger.LogDebug("The automatic {Kind} update to {To} on server {ServerId} was not started: {Code}.", kind, toVersion, server.Id, result.Code);
        return false;
    }
}

/// <summary>Runs <see cref="IPluginAutoUpdater"/> every few minutes. A failed pass is logged and tried again next time.</summary>
public class PluginAutoUpdateService(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<PluginAutoUpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);

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
                await scope.ServiceProvider.GetRequiredService<IPluginAutoUpdater>().RunPassAsync(clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automatic plugin update pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
