// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The plugin files on a server that Carbon says did not load, and why: what the Panel shows under "Failed to load" on the Plugins tab. Gated like
/// reading the server (the plugin list beside it is); it says nothing about players.
/// </summary>
/// <remarks>
/// The rows are the server's current set as of the last plugin-list poll, so a fixed plugin disappears by itself. Empty for a server that is not
/// running Carbon, has nothing failing, or has not been polled yet. Reads through the tenant-filtered repository, so another organization's server
/// id is a plain 404, never an empty list that would confirm it exists.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/plugin-failures")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerGet)]
public class ServerPluginFailuresController(IRustServerRepository servers, IPluginLoadFailureRepository failures) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ServerPluginFailureDto>>> Get(Guid id)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        var held = await failures.GetForServerAsync(id);
        return Ok(held.Select(f => new ServerPluginFailureDto
        {
            FileName = f.FileName,
            Line = f.Line,
            Column = f.Column,
            Message = f.Message,
            CapturedAtUtc = f.CapturedAtUtc
        }).ToList());
    }
}
