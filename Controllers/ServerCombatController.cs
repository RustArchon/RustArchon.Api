// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The combat log for one server: what the RustArchon plugin recorded, newest first, filterable by time window and by
/// player. Read-only; the events arrive by way of the Worker (see <c>PluginCombatEventsCapturedConsumer</c>).
/// </summary>
/// <remarks>
/// A separate controller from <see cref="RustServersController"/> on purpose: that one's constructor already carries a
/// dozen collaborators, and this endpoint needs only one more. It is gated by the same permission as reading a server
/// (<c>RustServer.Get</c>) and reads through the tenant-filtered repository, so another organization's server id is a
/// plain 404 - never an empty list that would confirm the server exists.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/combat")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerGet)]
public class ServerCombatController(IRustServerRepository servers, IPluginCombatChunkRepository chunks) : ControllerBase
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <param name="since">Only events at or after this time.</param>
    /// <param name="until">Only events at or before this time.</param>
    /// <param name="playerId">Only events where this SteamID64 is the attacker or the victim.</param>
    /// <param name="limit">Page size, 1 to 500 (default 100).</param>
    [HttpGet]
    public async Task<ActionResult<CombatLogDto>> Get(
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

        return Ok(await chunks.QueryAsync(id, since, until, playerId, Math.Clamp(limit, 1, MaxLimit)));
    }
}
