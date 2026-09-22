// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// A server's settings for having updates to its third-party plugins applied automatically, and where it stands on the three gates (plan,
/// opt-in, days before the monthly wipe). Separate from the server's full-record PUT so an ordinary edit can never reset them.
/// </summary>
[ApiController]
[Route("api/rustservers/{id:guid}/third-party-update-settings")]
[Authorize]
public class ServerThirdPartyUpdateSettingsController(
    IRustServerRepository servers, IThirdPartyPluginUpdateGate gate, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionCatalog.ServerGet)]
    public async Task<ActionResult<ThirdPartyPluginUpdateSettingsDto>> Get(Guid id)
    {
        var server = await servers.GetByIdAsync(id, null);
        if (server is null)
        {
            return NotFound();
        }

        return Ok(await ToDtoAsync(server));
    }

    /// <summary>
    /// Saves the opt-in and the days before the wipe. Turning the opt-in <b>on</b> is refused when the plan does not offer the feature - there
    /// is nothing to opt in to, and a stored "yes" would take effect by surprise if the organization later moved to a plan that does. Turning it
    /// off is always allowed.
    /// </summary>
    [HttpPut]
    [RequirePermission(PermissionCatalog.ServerUpdate)]
    public async Task<ActionResult<ThirdPartyPluginUpdateSettingsDto>> Put(Guid id, [FromBody] UpdateThirdPartyPluginUpdateSettingsDto settings)
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

        var current = await gate.EvaluateAsync(server, clock.GetUtcNow());
        if (settings.Enabled == true && !current.PlanOffers)
        {
            return BadRequest(new { message = "This organization's plan does not include automatic third-party plugin updates." });
        }

        server.ThirdPartyPluginUpdatesEnabled = settings.Enabled!.Value;
        server.ThirdPartyPluginUpdateHoldDays = settings.HoldDays!.Value;
        server = await servers.UpdateAsync(server);

        return Ok(await ToDtoAsync(server));
    }

    private async Task<ThirdPartyPluginUpdateSettingsDto> ToDtoAsync(RustServer server)
    {
        var result = await gate.EvaluateAsync(server, clock.GetUtcNow());
        return new ThirdPartyPluginUpdateSettingsDto
        {
            PlanOffers = result.PlanOffers,
            Enabled = result.OptedIn,
            HoldDays = result.HoldDays,
            MaxHoldDays = MonthlyWipeSchedule.MaxHoldDays,
            NextWipeUtc = result.NextWipeUtc,
            HeldUntilUtc = result.HeldUntilUtc,
            State = result.State switch
            {
                ThirdPartyUpdateGateState.Open => "open",
                ThirdPartyUpdateGateState.PlanDoesNotOffer => "plan_does_not_offer",
                ThirdPartyUpdateGateState.NotOptedIn => "not_opted_in",
                _ => "held_for_wipe"
            }
        };
    }
}
