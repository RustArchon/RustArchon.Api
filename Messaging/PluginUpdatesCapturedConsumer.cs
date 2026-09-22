// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Keeps the update notices a server's RustArchon plugin heard from UpdateChecker, and logs each one that is news with everything it said
/// (so the record survives even if the row is later replaced). A report that cannot be stored is logged and dropped, not retried: the
/// Worker reports again whenever the list changes.
/// </summary>
/// <remarks>
/// When a notice is new, or moves to a newer version, the server's watchers are told so (the Plugins tab's chip re-reads). A repeat of
/// what is already held only refreshes its times and says nothing: the Worker reports again on every change, and a chip that refreshed
/// for each would do so for no reason.
/// </remarks>
public class PluginUpdatesCapturedConsumer(
    IPluginUpdateNoticeRepository notices,
    IHubContext<RconHub> hub,
    TimeProvider clock,
    ILogger<PluginUpdatesCapturedConsumer> logger) : IConsumer<PluginUpdatesCaptured>
{
    public async Task Consume(ConsumeContext<PluginUpdatesCaptured> context)
    {
        var message = context.Message;
        if (message.Updates.Count == 0)
        {
            return;
        }

        try
        {
            var changes = await notices.MergeAsync(message.TenantId, message.ServerId, message.Updates, clock.GetUtcNow());

            foreach (var notice in changes.Added)
            {
                Log(message.ServerId, notice, "New");
            }

            foreach (var notice in changes.Advanced)
            {
                Log(message.ServerId, notice, "Newer");
            }

            if (changes.Added.Count + changes.Advanced.Count > 0)
            {
                await TellWatchersAsync(message.ServerId);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Dropped a plugin update report from server {ServerId}.", message.ServerId);
        }
    }

    // Only "something changed" goes down the hub, never the notice: the Panel re-reads through the endpoint, which checks the permission.
    // Failing to say so must not undo a notice that is already stored.
    private async Task TellWatchersAsync(Guid serverId)
    {
        try
        {
            await hub.Clients.Group(RconHub.GroupName(serverId)).SendAsync("ReceivePluginUpdatesChanged");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Telling watchers of server {ServerId} about a plugin update failed; it is stored regardless.", serverId);
        }
    }

    private void Log(Guid serverId, PluginUpdateNotice notice, string kind) =>
        logger.LogInformation(
            "{Kind} plugin update reported by UpdateChecker on server {ServerId}: {Plugin} {ReportedVersion} -> {LatestVersion} ({Marketplace}) {Url}, first heard {FirstSeenUtc:o}, heard {TimesSeen} time(s).",
            kind, serverId, notice.Name, notice.CurrentVersion, notice.LatestVersion, notice.Marketplace, notice.Url, notice.FirstSeenUtc, notice.TimesSeen);
}
