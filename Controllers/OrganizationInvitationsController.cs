// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Administration;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// An Organization inviting people to join it.
/// </summary>
/// <remarks>
/// The tenant comes from <see cref="ITenantContext"/>, as in the other two
/// <c>api/organization/*</c> controllers, so nobody can invite somebody into an Organization that is
/// not theirs. Gated by the same permission as managing members, because that is what this is - the
/// only way the membership list grows to include somebody who could not already reach it.
/// </remarks>
[ApiController]
[Route("api/organization/invitations")]
[Authorize]
[RequirePermission(PermissionCatalog.OrganizationManageMembers)]
public class OrganizationInvitationsController(
    IOrganizationInvitationService invitations,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>Invitations sent but not yet taken up.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OrganizationInvitationDto>>> List(
        CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return Ok(await invitations.ListAsync(tenantId, cancellationToken));
    }

    /// <summary>Invites somebody, and sends them the link.</summary>
    [HttpPost]
    public async Task<ActionResult<OrganizationInvitationDto>> Invite(
        [FromBody] InviteMemberRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        try
        {
            return Ok(await invitations.InviteAsync(
                tenantId, userId, request.Email, request.RoleId, cancellationToken));
        }
        catch (MemberManagementException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Withdraws an invitation.</summary>
    [HttpDelete("{invitationId:guid}")]
    public async Task<IActionResult> Revoke(Guid invitationId, CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        try
        {
            await invitations.RevokeAsync(tenantId, invitationId, userId, cancellationToken);
            return NoContent();
        }
        catch (MemberManagementException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
