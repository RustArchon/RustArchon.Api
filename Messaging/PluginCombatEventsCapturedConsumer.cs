// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Stores a batch of combat events the Worker drained from a server's RustArchon plugin. A malformed batch is logged and
/// dropped, never retried: the same bytes would fail the same way forever, and blocking behind it would stop every later
/// batch for that server. No SignalR relay: the Combat tab reads what is stored when it is opened or refreshed.
/// </summary>
public class PluginCombatEventsCapturedConsumer(
    IPluginCombatChunkRepository chunks,
    TimeProvider clock,
    ILogger<PluginCombatEventsCapturedConsumer> logger) : IConsumer<PluginCombatEventsCaptured>
{
    public async Task Consume(ConsumeContext<PluginCombatEventsCaptured> context)
    {
        var message = context.Message;

        try
        {
            var stored = await chunks.AppendAsync(
                message.TenantId, message.ServerId, message.BootId, message.Lost || message.Reset, message.EventsJson, clock.GetUtcNow());

            if (stored > 0)
            {
                logger.LogDebug(
                    "Stored {Stored} of {Received} combat events for server {ServerId} (boot {BootId}, sequences {First}-{Last}).",
                    stored, message.EventCount, message.ServerId, message.BootId, message.FirstSequence, message.LastSequence);
            }
        }
        catch (CombatChunkCodec.InvalidCombatBatchException ex)
        {
            logger.LogWarning(
                "Dropped a malformed combat batch from server {ServerId} (boot {BootId}, {Count} events): {Reason}",
                message.ServerId, message.BootId, message.EventCount, ex.Message);
        }
    }
}
