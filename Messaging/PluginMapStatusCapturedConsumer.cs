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
/// Records what the Worker read from a server's plugin about its world map, then, if the picture needs collecting, asks the
/// server to upload it (see <see cref="PluginMapUploadRequester"/>). A report that cannot be stored is logged and dropped, not
/// retried: the Worker reports again in a few minutes anyway.
/// </summary>
public class PluginMapStatusCapturedConsumer(
    IPluginMapRepository maps,
    IPluginMapUploadRequester requester,
    ILogger<PluginMapStatusCapturedConsumer> logger) : IConsumer<PluginMapStatusCaptured>
{
    /// <summary>The most monument JSON that is stored; a real list is a few kilobytes.</summary>
    public const int MaxMonumentsJsonLength = 200_000;

    public async Task Consume(ConsumeContext<PluginMapStatusCaptured> context)
    {
        var message = context.Message;

        if (message.WorldSize <= 0 || message.WorldSeed < 0 || string.IsNullOrEmpty(message.FileName))
        {
            logger.LogWarning("Dropped a map status from server {ServerId} that names no world.", message.ServerId);
            return;
        }

        var monuments = message.MonumentsJson;
        if (monuments is not null && monuments.Length > MaxMonumentsJsonLength)
        {
            logger.LogWarning("Ignored an oversized monument list ({Length} characters) from server {ServerId}.", monuments.Length, message.ServerId);
            monuments = null;
        }

        try
        {
            var map = await maps.UpsertStatusAsync(
                message.TenantId, message.ServerId, message.WorldSize, message.WorldSeed, message.FileName,
                message.Exists, message.Bytes, monuments, message.CapturedAtUtc);

            await requester.RequestIfNeededAsync(map, message.UploadState);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Dropped a map status from server {ServerId}.", message.ServerId);
        }
    }
}
