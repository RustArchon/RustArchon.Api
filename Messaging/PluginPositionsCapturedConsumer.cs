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
/// Stores a batch of position samples the Worker drained from a server's RustArchon plugin. A malformed batch is logged and
/// dropped, never retried: the same bytes would fail the same way forever, and blocking behind it would stop every later
/// batch for that server.
/// </summary>
public class PluginPositionsCapturedConsumer(
    IPluginPositionChunkRepository chunks,
    TimeProvider clock,
    ILogger<PluginPositionsCapturedConsumer> logger) : IConsumer<PluginPositionsCaptured>
{
    public async Task Consume(ConsumeContext<PluginPositionsCaptured> context)
    {
        var message = context.Message;

        try
        {
            var stored = await chunks.AppendAsync(
                message.TenantId, message.ServerId, message.BootId, message.Lost || message.Reset, message.SamplesJson, clock.GetUtcNow());

            if (stored > 0)
            {
                logger.LogDebug(
                    "Stored {Stored} of {Received} position samples for server {ServerId} (boot {BootId}, sequences {First}-{Last}).",
                    stored, message.SampleCount, message.ServerId, message.BootId, message.FirstSequence, message.LastSequence);
            }
        }
        catch (PositionChunkCodec.InvalidPositionBatchException ex)
        {
            logger.LogWarning(
                "Dropped a malformed position batch from server {ServerId} (boot {BootId}, {Count} samples): {Reason}",
                message.ServerId, message.BootId, message.SampleCount, ex.Message);
        }
    }
}
