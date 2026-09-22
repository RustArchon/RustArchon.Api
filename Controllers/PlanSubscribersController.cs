// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Moving a plan's current subscribers to a newer version of it. Gated like the rest of plan management. A subscriber is moved only when the newer
/// version is no worse for them (see <see cref="PlanMoveAssessment"/>); the rest stay where they are, and the answer says how many and why.
/// </summary>
[ApiController]
[Route("api/plans/{id:guid}")]
[Authorize(Policy = "ManagePlans")]
public class PlanSubscribersController(IPlanRepository plans, IPlanSubscriberMover mover, IUserContext userContext) : ControllerBase
{
    /// <summary>
    /// What superseding this plan with these terms would do to its current subscribers if they were moved along - asked <em>before</em> superseding, so the
    /// admin can decide. Nothing is created or changed.
    /// </summary>
    [HttpPost("supersede/preview")]
    public async Task<ActionResult<PlanMovePreviewDto>> PreviewSupersede(Guid id, [FromBody] SupersedePlanDto terms, CancellationToken cancellationToken)
    {
        var current = await plans.GetWithPricesAsync(id);
        if (current is null)
        {
            return NotFound();
        }

        // The terms are judged as the plan they would become; it is not saved.
        var proposed = PlansController.NewVersion(current.Name, terms);
        return Ok(await mover.PreviewAsync(current, proposed, cancellationToken));
    }

    /// <summary>What moving this replaced plan's current subscribers to its latest version would do. 409 if it has not been replaced or the latest version is not on offer.</summary>
    [HttpGet("move-preview")]
    public async Task<ActionResult<PlanMovePreviewDto>> PreviewMove(Guid id, CancellationToken cancellationToken)
    {
        var (current, latest, problem) = await ResolveAsync(id);
        return problem is not null ? problem : Ok(await mover.PreviewAsync(current!, latest!, cancellationToken));
    }

    /// <summary>
    /// Moves this replaced plan's current subscribers to its latest version - those it is no worse for. Everyone else stays. Safe to repeat: it only ever
    /// finds people still on this version.
    /// </summary>
    [HttpPost("move-subscribers")]
    public async Task<ActionResult<PlanMoveResultDto>> MoveSubscribers(Guid id, CancellationToken cancellationToken)
    {
        var (current, latest, problem) = await ResolveAsync(id);
        if (problem is not null)
        {
            return problem;
        }

        return Ok(await mover.MoveAsync(current!, latest!, await userContext.GetCurrentUserIdAsync(), cancellationToken));
    }

    // The plan and where it leads: it has to be a replaced plan, and the end of its chain has to be a plan that is on offer - moving people onto a plan
    // that new sign-ups cannot choose would strand them on something the owner has withdrawn.
    private async Task<(RustArchon.Api.Data.Plan? Current, RustArchon.Api.Data.Plan? Latest, ActionResult? Problem)> ResolveAsync(Guid id)
    {
        var current = await plans.GetWithPricesAsync(id);
        if (current is null)
        {
            return (null, null, NotFound());
        }

        if (current.SupersededByPlanId is null)
        {
            return (null, null, Conflict(new { message = "This plan has not been replaced by a newer version, so there is nowhere to move its subscribers." }));
        }

        var latest = await plans.GetLatestVersionAsync(id);
        if (latest is null || latest.Id == id || !latest.Active)
        {
            return (null, null, Conflict(new { message = "The latest version of this plan is not active, so subscribers are not moved onto it." }));
        }

        return (current, latest, null);
    }
}
