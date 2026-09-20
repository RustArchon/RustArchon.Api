// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Stores the Worker's latest read of a server's tool cupboard index as that server's current snapshot. A malformed one is
/// logged and dropped, never retried: the same bytes would fail the same way forever, and the next read (a minute later)
/// replaces it anyway.
/// </summary>
public class PluginTcSnapshotCapturedConsumer(
    IPluginTcSnapshotRepository snapshots,
    ILogger<PluginTcSnapshotCapturedConsumer> logger) : IConsumer<PluginTcSnapshotCaptured>
{
    public async Task Consume(ConsumeContext<PluginTcSnapshotCaptured> context)
    {
        var message = context.Message;

        try
        {
            await snapshots.ReplaceAsync(message.TenantId, message.ServerId, message.Ready, message.TcsJson, message.CapturedAtUtc);
        }
        catch (TcSnapshotCodec.InvalidTcSnapshotException ex)
        {
            logger.LogWarning(
                "Dropped a malformed tool cupboard snapshot from server {ServerId} ({Count} cupboards): {Reason}",
                message.ServerId, message.Count, ex.Message);
        }
    }
}
