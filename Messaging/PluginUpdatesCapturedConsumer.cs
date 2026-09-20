// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Keeps the update notices a server's RustArchon plugin heard from UpdateChecker, and logs each one that is news with everything it said
/// (so the record survives even if the row is later replaced). A report that cannot be stored is logged and dropped, not retried: the
/// Worker reports again whenever the list changes.
/// </summary>
public class PluginUpdatesCapturedConsumer(
    IPluginUpdateNoticeRepository notices,
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
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Dropped a plugin update report from server {ServerId}.", message.ServerId);
        }
    }

    private void Log(Guid serverId, PluginUpdateNotice notice, string kind) =>
        logger.LogInformation(
            "{Kind} plugin update reported by UpdateChecker on server {ServerId}: {Plugin} {ReportedVersion} -> {LatestVersion} ({Marketplace}) {Url}, first heard {FirstSeenUtc:o}, heard {TimesSeen} time(s).",
            kind, serverId, notice.Name, notice.CurrentVersion, notice.LatestVersion, notice.Marketplace, notice.Url, notice.FirstSeenUtc, notice.TimesSeen);
}
