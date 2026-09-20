// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure;

/// <summary>Starts a Panel-triggered update of the RustArchon plugin on one game server.</summary>
public interface IPluginUpdateService
{
    /// <summary>
    /// Checks every precondition, mints a single-use token, and tells the server's Updater plugin (through the Worker)
    /// to fetch and verify the current version. Never throws for a refusal - see <see cref="PluginUpdateResultDto.Code"/>.
    /// </summary>
    Task<PluginUpdateResultDto> StartAsync(RustServer server);
}

/// <inheritdoc cref="IPluginUpdateService" />
/// <remarks>
/// <para>
/// <b>Fails closed at every step, and mints the token last.</b> An update replaces code that runs with full server
/// privileges, so it is refused unless the admin turned updates on for this server, the installed plugin is signed
/// by a key this Panel has (its active key, or a retired one - the file is then a bridge to the active key), that key
/// is not revoked, the Updater is present (and new enough to accept a bridge, if one is needed), and the version this
/// Panel serves is strictly newer. Only then is a token minted, and it is discarded again if the Updater
/// refuses, so no unused permission is left lying around.
/// </para>
/// <para>
/// The command goes through the Worker's existing <see cref="SendRconCommand"/> pathway, non-interactive: it is the
/// Panel's own action, not something a person typed, and the Worker already flags such frames so only the site owner
/// sees them (the token is in the URL).
/// </para>
/// </remarks>
public class PluginUpdateService(
    IServerPluginStatusRepository statuses,
    IServerPluginRepository plugins,
    IPluginScriptService script,
    IPluginUpdateTokenRepository tokens,
    IPlatformSettingsCache settings,
    IRequestClient<SendRconCommand> sendCommandClient,
    ILogger<PluginUpdateService> logger) : IPluginUpdateService
{
    /// <summary>How long a token stays valid: the Updater downloads within seconds, so this is generous.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The first Updater that anchors trust to the installed plugin and accepts a key bridge.</summary>
    public const string MinimumUpdaterVersionForBridge = "0.2.0";

    /// <summary>
    /// The first Updater that takes the download token as a separate argument and sends it in a header. An older one is given the
    /// token inside the address, as it always was.
    /// </summary>
    public const string MinimumUpdaterVersionForHeaderToken = "0.3.0";

    public async Task<PluginUpdateResultDto> StartAsync(RustServer server)
    {
        if (!server.IsEnabled)
        {
            return Refused("server_disabled", "This server is disabled.");
        }

        if (!server.PluginUpdatesEnabled)
        {
            return Refused("updates_disabled", "Plugin updates are not enabled for this server.");
        }

        var status = await statuses.GetForServerAcrossTenantsAsync(server.TenantId, server.Id);
        if (status is null)
        {
            return Refused("no_handshake", "The plugin has not reported in yet.");
        }

        // The plugin's own check said valid AND names a key this Panel has. Neither alone is enough: a plugin signed by
        // another Panel would be told to fetch a file its Updater will refuse. Unknown is refused (fail closed).
        var keyState = status.SigningState == PluginSigningStates.Valid
            ? await script.GetKeyStateAsync(status.SigningKeyFingerprint)
            : null;
        if (keyState == PluginKeyState.Revoked)
        {
            return Refused(
                "key_revoked",
                "The key this plugin was signed with has been revoked, so it cannot be updated from here. Download the plugin from this Panel and install it by hand.",
                status.PluginVersion);
        }

        if (keyState is null)
        {
            return Refused(
                "not_signed_by_this_panel",
                "The installed plugin was not signed by this Panel, so it cannot be updated from here. Download it from this Panel and install it by hand.",
                status.PluginVersion);
        }

        var installedPlugins = await plugins.GetForServerAsync(server.Id) ?? [];
        var updater = installedPlugins.FirstOrDefault(p => string.Equals(p.Name, RustArchonPlugin.UpdaterName, StringComparison.OrdinalIgnoreCase));
        if (updater is null)
        {
            return Refused("updater_missing", "The RustArchon Updater plugin is not installed on this server.", status.PluginVersion);
        }

        // Moving a server to a newer key is a "bridge", which only an Updater from 0.2.0 on will accept. An older one
        // would download it and refuse, so say so first. The Updater is only ever replaced by hand.
        if (keyState == PluginKeyState.Retired && !PluginVersions.IsAtLeast(updater.Version?.TrimStart('v', 'V'), MinimumUpdaterVersionForBridge))
        {
            return Refused(
                "updater_too_old",
                $"This server's signing key has been replaced, and its Updater ({updater.Version}) is too old to move it to the new key. Download the current Updater and the plugin from this Panel and install them by hand.",
                status.PluginVersion);
        }

        var latest = await script.GetLatestVersionAsync();
        if (!PluginVersions.IsNewer(latest, status.PluginVersion))
        {
            return Refused("up_to_date", "The plugin is already the newest version this Panel serves.", status.PluginVersion, latest);
        }

        var baseUrl = await settings.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var panelUri) || (panelUri.Scheme != Uri.UriSchemeHttp && panelUri.Scheme != Uri.UriSchemeHttps))
        {
            return Refused(
                "panel_url_invalid",
                "The Panel base URL platform setting is not set to an address the game server can reach.",
                status.PluginVersion,
                latest);
        }

        // The token remembers which key the server trusts, so what it downloads is signed with that one.
        var token = await tokens.MintAsync(server.TenantId, server.Id, status.SigningKeyFingerprint.ToLowerInvariant(), TokenLifetime);
        var panelBase = $"{panelUri.GetLeftPart(UriPartial.Authority)}{panelUri.AbsolutePath.TrimEnd('/')}";
        var updateCommand = PluginVersions.IsAtLeast(updater.Version?.TrimStart('v', 'V'), MinimumUpdaterVersionForHeaderToken)
            ? $"archon.update {latest} {panelBase}/ingest/plugin {token}"
            : $"archon.update {latest} {panelBase}/ingest/plugin/{server.Id}/{token}";

        try
        {
            var response = await sendCommandClient.GetResponse<RconCommandResult>(
                new SendRconCommand(server.Id, updateCommand, Interactive: false),
                timeout: RequestTimeout.After(s: 10));

            if (!response.Message.Success)
            {
                await tokens.RevokeAsync(token);
                return Refused("not_connected", "The server is not connected right now.", status.PluginVersion, latest);
            }

            var (accepted, code, message) = ParseReply(response.Message.Message);
            if (!accepted)
            {
                await tokens.RevokeAsync(token);
                return Refused(code, message, status.PluginVersion, latest);
            }

            logger.LogInformation(
                "Started RustArchon plugin update {From} -> {To} on server {ServerId}.", status.PluginVersion, latest, server.Id);
            return new PluginUpdateResultDto
            {
                Started = true,
                Code = "started",
                Message = "The Updater accepted the update and is downloading it.",
                FromVersion = status.PluginVersion,
                ToVersion = latest
            };
        }
        catch (RequestTimeoutException)
        {
            await tokens.RevokeAsync(token);
            return Refused("timeout", "The server did not answer in time.", status.PluginVersion, latest);
        }
    }

    // The Updater answers with an envelope: {"v":1,"ok":true,...} or {"v":1,"ok":false,"err":"...","message":"..."}.
    // Anything that is not clearly ok is a refusal (fail closed), whatever the text.
    private static (bool Accepted, string Code, string Message) ParseReply(string? reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply ?? "");
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return (true, "started", "");
            }

            var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String
                ? err.GetString()
                : null;
            var message = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                ? msg.GetString()
                : null;
            return (false, Sanitize(code) ?? "updater_refused", message ?? "The Updater refused the request.");
        }
        catch (JsonException)
        {
            // Not an envelope at all: most likely "Unknown command", i.e. the Updater is not actually loaded.
            return (false, "updater_missing", "The server did not recognize the update command; the Updater plugin may not be loaded.");
        }
    }

    // The code comes from a plugin on someone else's server and is shown to a user: short and simple, or dropped.
    private static string? Sanitize(string? code) =>
        code is { Length: > 0 and <= 40 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : null;

    private static PluginUpdateResultDto Refused(string code, string message, string? from = null, string? to = null) =>
        new() { Started = false, Code = code, Message = message, FromVersion = from, ToVersion = to };
}
