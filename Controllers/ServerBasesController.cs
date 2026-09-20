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
/// Where a server's players' bases are: every tool cupboard the RustArchon plugin has indexed, with its owner and who is
/// authorized. Sensitive - it says where people live and who they share with - so it is gated by its own permission,
/// <see cref="PermissionCatalog.ServerViewBases"/>, which an Organization's Owner holds and can delegate to a role, and
/// every read is logged with who made it.
/// </summary>
/// <remarks>
/// Not <c>RustServer.Get</c>: a moderator who may look at a server must not thereby be able to see where every base is.
/// Reads through the tenant-filtered repository, so another organization's server id is a plain 404.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/bases")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerViewBases)]
public class ServerBasesController(
    IRustServerRepository servers, IPluginTcSnapshotRepository snapshots, IPlayerSessionRepository sessions,
    ILogger<ServerBasesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<BasesDto>> Get(Guid id)
    {
        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        // The audit trail for a sensitive read: who looked at which server's bases, and when (the log's own timestamp).
        logger.LogInformation(
            "Bases of server {ServerId} viewed by {User}.", id,
            User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? User.Identity?.Name ?? "unknown");

        var bases = await snapshots.GetAsync(id);
        await NameThePlayersAsync(server.TenantId, id, bases);
        return Ok(bases);
    }

    /// <summary>
    /// The plugin can only name players who are in the world right now, so anyone else arrives as a bare id. Fills the gap from the
    /// names the Api has recorded for those players' sessions on this server (they had to connect to be on a cupboard, and the Worker
    /// records every connection). What the plugin named stays as it was; a player nothing has a name for stays an id.
    /// </summary>
    private async Task NameThePlayersAsync(Guid tenantId, Guid serverId, BasesDto bases)
    {
        var unnamed = bases.Tcs
            .SelectMany(tc => tc.Authorized.Where(a => string.IsNullOrEmpty(a.Name)).Select(a => a.PlayerId).Append(tc.OwnerId))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        if (unnamed.Count == 0)
        {
            return;
        }

        var names = await sessions.GetLatestNamesAsync(tenantId, serverId, unnamed);

        foreach (var tc in bases.Tcs)
        {
            foreach (var authorized in tc.Authorized.Where(a => string.IsNullOrEmpty(a.Name)))
            {
                if (names.TryGetValue(authorized.PlayerId, out var name))
                {
                    authorized.Name = name;
                }
            }

            var owner = tc.Authorized.FirstOrDefault(a => a.PlayerId == tc.OwnerId && !string.IsNullOrEmpty(a.Name));
            tc.OwnerName = owner?.Name ?? (names.TryGetValue(tc.OwnerId, out var ownerName) ? ownerName : string.Empty);
        }
    }
}
