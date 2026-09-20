// Copyright ©2026 Scott Blomfield

using System;
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
    IRustServerRepository servers, IPluginTcSnapshotRepository snapshots, ILogger<ServerBasesController> logger) : ControllerBase
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

        return Ok(await snapshots.GetAsync(id));
    }
}
