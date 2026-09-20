// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>The recent plugin and Updater updates of a server and how they turned out. Gated like reading the server.</summary>
[ApiController]
[Route("api/rustservers/{id:guid}/plugin-update-attempts")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerGet)]
public class ServerPluginUpdateAttemptsController(IRustServerRepository servers, IPluginUpdateAttemptRepository attempts) : ControllerBase
{
    /// <summary>The most attempts returned; the page shows the last few.</summary>
    public const int MaxReturned = 20;

    [HttpGet]
    public async Task<ActionResult<List<PluginUpdateAttemptDto>>> Get(Guid id)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        var recent = await attempts.GetRecentForServerAsync(id, MaxReturned);
        return Ok(recent.Select(a => new PluginUpdateAttemptDto
        {
            Kind = a.Kind, FromVersion = a.FromVersion, ToVersion = a.ToVersion, Trigger = a.Trigger, State = a.State, Code = a.Code,
            StartedAtUtc = a.StartedAtUtc, ResolvedAtUtc = a.ResolvedAtUtc
        }).ToList());
    }
}
