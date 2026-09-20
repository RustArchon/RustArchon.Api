// Copyright ©2026 Scott Blomfield

using System;
using System.Text.Json;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Infrastructure;

/// <summary>Decides whether a server's world map picture needs collecting and, if so, asks the server to send it.</summary>
public interface IPluginMapUploadRequester
{
    /// <summary>Returns true when an upload was requested (the server accepted the command).</summary>
    Task<bool> RequestIfNeededAsync(PluginMap map, string uploadState);
}

/// <inheritdoc cref="IPluginMapUploadRequester" />
/// <remarks>
/// <para>
/// The Api decides and the Worker executes, as for plugin updates: the Worker only reports what the plugin says
/// (<see cref="PluginMapStatusCaptured"/>) and delivers the command. The Api mints the one-time token because it owns the
/// database that will check it, and the game server holds no standing credential: the token in the command is the
/// credential, good once, for one server and one map, for ten minutes.
/// </para>
/// <para>
/// A picture is wanted when the game server has one, the Api has none (or a different size, i.e. it was redrawn), and the
/// plugin is not already uploading. Asking is throttled to once per <see cref="Cooldown"/> per map, so a server that
/// cannot upload (the Panel address is unreachable from it, say) is retried a few times an hour, not on every poll.
/// </para>
/// </remarks>
public class PluginMapUploadRequester(
    IPluginMapRepository maps,
    IPluginMapUploadTokenRepository tokens,
    IPlatformSettingsCache settings,
    IRequestClient<SendRconCommand> sendCommandClient,
    TimeProvider clock,
    ILogger<PluginMapUploadRequester> logger) : IPluginMapUploadRequester
{
    /// <summary>How long a token stays valid. The upload starts within seconds; a 24 MB picture takes a few more.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The least time between two requests for the same map.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    public async Task<bool> RequestIfNeededAsync(PluginMap map, string uploadState)
    {
        if (!map.ExistsOnServer || map.ServerBytes <= 0)
        {
            return false;
        }

        if (string.Equals(uploadState, "uploading", StringComparison.Ordinal))
        {
            return false;
        }

        var have = map.UploadedAtUtc is not null && map.UploadedBytes == map.ServerBytes;
        if (have)
        {
            return false;
        }

        var baseUrl = await settings.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var panelUri) || (panelUri.Scheme != Uri.UriSchemeHttp && panelUri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("Not collecting the map for server {ServerId}: the Panel base URL platform setting is not a usable address.", map.RustServerId);
            return false;
        }

        // The gate: of any number of simultaneous reports, one gets to ask.
        if (!await maps.TryClaimUploadRequestAsync(map.Id, clock.GetUtcNow(), Cooldown))
        {
            return false;
        }

        var token = await tokens.MintAsync(map.TenantId, map.RustServerId, map.Id, TokenLifetime);
        // The address is constant; the token (which names the server and map) goes in a header on the plugin's request.
        var url = $"{panelUri.GetLeftPart(UriPartial.Authority)}{panelUri.AbsolutePath.TrimEnd('/')}/ingest/plugin-map";

        try
        {
            var response = await sendCommandClient.GetResponse<RconCommandResult>(
                new SendRconCommand(map.RustServerId, $"archon.map.upload {url} {token}", Interactive: false),
                timeout: RequestTimeout.After(s: 10));

            if (!response.Message.Success || !Accepted(response.Message.Message))
            {
                await tokens.RevokeAsync(token);
                logger.LogInformation("Server {ServerId} did not accept the map upload request: {Reply}", map.RustServerId, response.Message.Message ?? response.Message.Error);
                return false;
            }

            logger.LogInformation("Asked server {ServerId} to upload its map ({File}, {Bytes} bytes).", map.RustServerId, map.FileName, map.ServerBytes);
            return true;
        }
        catch (RequestTimeoutException)
        {
            await tokens.RevokeAsync(token);
            logger.LogInformation("Server {ServerId} did not answer the map upload request in time.", map.RustServerId);
            return false;
        }
    }

    // The plugin answers {"v":1,"ok":true,...}. Anything that is not clearly ok is a refusal (fail closed).
    private static bool Accepted(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
