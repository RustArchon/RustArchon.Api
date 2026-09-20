// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Where players were and are: the positions the RustArchon plugin recorded, newest first, filterable by time window and by
/// player. Sensitive - it says where every player is, right now - so it is gated by its own permission,
/// <see cref="PermissionCatalog.ServerViewPositions"/>, which an Organization's Owner holds and can delegate to a role, and
/// every read is logged with who made it.
/// </summary>
/// <remarks>
/// Not <c>RustServer.Get</c>, for the same reason as the bases list. Reads through the tenant-filtered repository, so another
/// organization's server id is a plain 404 - never an empty list that would confirm the server exists.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/positions")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerViewPositions)]
public class ServerPositionsController(
    IRustServerRepository servers, IPluginPositionChunkRepository chunks, ILogger<ServerPositionsController> logger) : ControllerBase
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    /// <param name="since">Only samples at or after this time.</param>
    /// <param name="until">Only samples at or before this time.</param>
    /// <param name="playerId">Only this SteamID64's samples.</param>
    /// <param name="limit">How many, 1 to 5000 (default 500).</param>
    [HttpGet]
    public async Task<ActionResult<PositionsDto>> Get(
        Guid id, [FromQuery] DateTimeOffset? since, [FromQuery] DateTimeOffset? until,
        [FromQuery] string? playerId, [FromQuery] int limit = DefaultLimit)
    {
        // A SteamID64 is digits only. Anything else cannot match a player, so say so rather than run a query for it.
        if (!string.IsNullOrEmpty(playerId) && (playerId.Length > 20 || !playerId.All(char.IsAsciiDigit)))
        {
            return BadRequest("The player id must be a SteamID64 (digits only).");
        }

        if (since is not null && until is not null && since > until)
        {
            return BadRequest("'since' must not be later than 'until'.");
        }

        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        // The audit trail for a sensitive read: who looked at which server's positions (and whose), and when (the log's timestamp).
        logger.LogInformation(
            "Positions of server {ServerId}{Player} viewed by {User}.", id, string.IsNullOrEmpty(playerId) ? "" : $" (player {playerId})",
            User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? User.Identity?.Name ?? "unknown");

        return Ok(await chunks.QueryAsync(id, since, until, playerId, Math.Clamp(limit, 1, MaxLimit)));
    }
}
