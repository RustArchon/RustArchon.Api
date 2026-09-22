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
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// What can be done about a server's outdated third-party plugins, and applying one by hand. Applying an update changes files on someone's game
/// server, so it takes the permission to update the server, not just to see it; what it does and what it checks first is
/// <see cref="ThirdPartyPluginUpdateService"/>'s.
/// </summary>
[ApiController]
[Route("api/rustservers/{id:guid}/third-party-updates")]
[Authorize]
public class ServerThirdPartyUpdatesController(
    IRustServerRepository servers, IThirdPartyPluginUpdateService updates, IPluginUpdateNoticeRepository notices, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionCatalog.ServerGet)]
    public async Task<ActionResult<List<ThirdPartyPluginUpdateOfferDto>>> Get(Guid id)
    {
        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        // An update under way is settled when someone looks, not only when the automatic pass next comes round, so the page shows the outcome
        // within seconds. Idempotent, and a no-op when nothing is under way.
        await updates.ReconcileAsync(server, clock.GetUtcNow());

        var offers = await updates.GetOffersAsync(server, clock.GetUtcNow());
        if (offers.Count == 0)
        {
            return Ok(new List<ThirdPartyPluginUpdateOfferDto>());
        }

        // The plugin's own capitalization, for showing; the offers are keyed by the normalized name.
        var names = (await notices.GetForServerAsync(id)).GroupBy(n => n.NormalizedName).ToDictionary(g => g.Key, g => g.First().Name);
        return Ok(offers.Select(o => new ThirdPartyPluginUpdateOfferDto
        {
            PluginName = names.GetValueOrDefault(o.Key, o.Key),
            State = o.Value.State,
            Detail = o.Value.Detail,
            FileSha256 = o.Value.FileSha256,
            CanApply = o.Value.CanApply,
            AutomaticUpdatesPausedUntilUtc = o.Value.HeldUntilUtc,
            Kind = o.Value.Kind,
            Files = [.. o.Value.Files ?? []],
            SavedRules = [.. o.Value.SavedRules ?? []],
            UncoveredPaths = [.. o.Value.UncoveredPaths ?? []],
            MappingTrusted = o.Value.MappingTrusted
        }).OrderBy(o => o.PluginName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Tells the server to apply one plugin's update now. Every precondition is checked first and a refusal comes back as an ordinary
    /// <see cref="PluginUpdateResultDto"/> with a <c>Code</c> saying why, so the page can explain it. Not held back by the server's opt-in or its
    /// days-before-wipe window: those govern what happens <i>automatically</i>, and this is a person's decision. The plan must still offer the feature.
    /// </summary>
    [HttpPost("apply")]
    [RequirePermission(PermissionCatalog.ServerUpdate)]
    public async Task<ActionResult<PluginUpdateResultDto>> Apply(Guid id, [FromBody] ApplyThirdPartyPluginUpdateDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        return Ok(await updates.StartAsync(server, request.PluginName!, Data.PluginUpdateTriggers.Manual, request.FileSha256, request.Rules, request.SaveMapping));
    }

    /// <summary>
    /// Downloads the file behind one plugin's update again, right now, instead of waiting for whatever a "changed" verdict already triggered, or for
    /// the periodic validation job, to look at it. Same permission as applying: it starts a real download from a third-party host on the platform's
    /// behalf. Returns whether there was anything to recheck - not a hard failure either way; the page re-reads the list afterward regardless.
    /// </summary>
    [HttpPost("recheck")]
    [RequirePermission(PermissionCatalog.ServerUpdate)]
    public async Task<ActionResult<bool>> Recheck(Guid id, [FromBody] RecheckThirdPartyPluginFileDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        return Ok(await updates.RecheckFileAsync(server, request.PluginName!));
    }

    /// <summary>
    /// Says that a plugin's update notice does not describe what is actually installed here (most often a free listing standing in for a different,
    /// paid build), so it is never applied - automatically or by a click - until the exclusion is lifted. Same permission as applying: it changes
    /// what would otherwise happen to a game server.
    /// </summary>
    [HttpPost("exclude")]
    [RequirePermission(PermissionCatalog.ServerUpdate)]
    public async Task<ActionResult> Exclude(Guid id, [FromBody] ExcludeThirdPartyPluginUpdateDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        await updates.ExcludeAsync(server, request.PluginName!, request.Note, clock.GetUtcNow());
        return Ok();
    }

    /// <summary>Lifts an exclusion, if there is one, so the plugin's update notice is offered again.</summary>
    [HttpPost("include")]
    [RequirePermission(PermissionCatalog.ServerUpdate)]
    public async Task<ActionResult> Include(Guid id, [FromBody] RecheckThirdPartyPluginFileDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        await updates.IncludeAsync(server, request.PluginName!);
        return Ok();
    }
}
