// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// A message from the site admin to the organizations on a plan - for what the platform cannot say for them, such as a change coming to their plan. Gated
/// like the rest of plan management. Sending is two steps by design: <c>preview</c> says exactly who would be emailed, and <c>send</c> is only accepted for the
/// number the admin was shown.
/// </summary>
[ApiController]
[Route("api/plans/{id:guid}/announcements")]
[Authorize(Policy = "ManagePlans")]
public class PlanAnnouncementsController(IPlanAnnouncementService announcements, IUserContext userContext) : ControllerBase
{
    /// <summary>Who these criteria would reach - no email is sent.</summary>
    [HttpPost("preview")]
    public async Task<ActionResult<AnnouncementPreviewDto>> Preview(Guid id, [FromBody] AnnouncementCriteriaDto criteria, CancellationToken cancellationToken)
    {
        var preview = await announcements.PreviewAsync(id, criteria, cancellationToken);
        return preview is null ? NotFound() : Ok(preview);
    }

    /// <summary>Emails each language's version of the message to one address, so the admin can see how it will look. Nothing goes to any organization.</summary>
    [HttpPost("test")]
    public async Task<ActionResult<AnnouncementResultDto>> Test(Guid id, [FromBody] SendAnnouncementTestDto request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var (queued, problem) = await announcements.SendTestAsync(id, request, cancellationToken);
        return problem is null ? Ok(new AnnouncementResultDto { Queued = queued }) : Problem(problem);
    }

    /// <summary>
    /// Emails the message to every organization the criteria reach. 409 when the number reached is not the <c>ExpectedRecipients</c> the admin confirmed
    /// (nothing is sent then); 400 when the words are not acceptable (a missing default-language version, an empty subject, an unknown token).
    /// </summary>
    [HttpPost("send")]
    public async Task<ActionResult<AnnouncementResultDto>> Send(Guid id, [FromBody] SendPlanAnnouncementDto request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var (result, problem) = await announcements.SendAsync(id, request, await userContext.GetCurrentUserIdAsync(), cancellationToken);
        return problem is null ? Ok(result) : Problem(problem);
    }

    /// <summary>The messages already sent from this plan, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AnnouncementBatchDto>>> History(Guid id, CancellationToken cancellationToken) =>
        Ok(await announcements.HistoryAsync(id, 20, cancellationToken));

    private ActionResult Problem(AnnouncementProblem problem) => problem.Code switch
    {
        "no_such_plan" => NotFound(new { code = problem.Code, message = problem.Message }),
        "audience_changed" => Conflict(new { code = problem.Code, message = problem.Message }),
        _ => BadRequest(new { code = problem.Code, message = problem.Message })
    };
}
