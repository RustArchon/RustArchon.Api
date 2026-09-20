// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The Api side of the Panel's public plugin download door: redeems a single-use token and, if it was good, returns the
/// signed plugin script. Not reachable from outside - authenticated by the shared internal-service key, called only by
/// the Panel's anonymous <c>/ingest/plugin/{serverId}/{token}</c> route, which is what a game server's Updater actually
/// contacts (see <see cref="InternalController"/>'s remarks for why the Api itself is never public).
/// </summary>
[ApiController]
[Route("internal/plugin")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalPluginController(
    IPluginUpdateTokenRepository tokens,
    IPluginScriptService script,
    ILogger<InternalPluginController> logger) : ControllerBase
{
    /// <summary>The longest token this will even look up. A real one is 43 characters.</summary>
    private const int MaxTokenLength = 128;

    /// <summary>
    /// Redeems <paramref name="token"/> for <paramref name="serverId"/> and returns the current main plugin script.
    /// </summary>
    /// <returns>
    /// The script, or a bare <c>404</c> for every refusal alike - unknown token, wrong server, already used, expired -
    /// so the response tells a guesser nothing. <c>503</c> only if the signing key is unusable.
    /// </returns>
    [HttpGet("download/{serverId:guid}/{token}")]
    public async Task<IActionResult> Download(Guid serverId, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return NotFound();
        }

        var redemption = await tokens.RedeemAsync(serverId, token);
        if (redemption is null)
        {
            return NotFound();
        }

        try
        {
            // Signed with the key the server's installed plugin trusts - a bridge to the active key if that is an older one.
            var built = await script.BuildBridgeAsync(redemption.SigningKeyFingerprint);
            Response.Headers["X-RustArchon-Plugin-Version"] = built.PluginVersion ?? "";
            Response.Headers.CacheControl = "no-store";
            return File(built.Bytes, "text/plain; charset=utf-8", "RustArchon.cs");
        }
        catch (PluginKeyUnavailableException ex)
        {
            // Revoked between the update being started and the download: the same bare answer as any other refusal.
            logger.LogWarning(ex, "A plugin update token was redeemed for server {ServerId} but its signing key is no longer usable.", serverId);
            return NotFound();
        }
        catch (PluginSigningKeyException ex)
        {
            logger.LogError(ex, "A plugin update token was redeemed for server {ServerId} but the script could not be signed.", serverId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
