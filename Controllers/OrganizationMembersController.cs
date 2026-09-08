// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
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
/// An Organization managing its own people.
/// </summary>
/// <remarks>
/// <para>
/// The tenant comes from <see cref="ITenantContext"/> - the verified <c>tenant_id</c> claim - and never
/// from the route or body, exactly as in <see cref="OrganizationRolesController"/>, so no request
/// reaches another Organization's membership. <c>OrganizationsController</c> is the endpoint that
/// crosses that boundary, and it is gated by a platform permission instead.
/// </para>
/// <para>
/// One permission covers the whole controller: somebody who can manage members can grant and revoke
/// the Organization's roles too. What stops that becoming a way to award yourself anything is not a
/// second permission but JumpStart's assignment rule - you cannot hand out a role containing a
/// permission you do not hold. Defining what a role <em>means</em> is the sharper act, and that needs
/// <c>Organization.ManageRoles</c> on the other controller.
/// </para>
/// </remarks>
[ApiController]
[Route("api/organization/members")]
[Authorize]
[RequirePermission(PermissionCatalog.OrganizationManageMembers)]
public class OrganizationMembersController(
    IOrganizationMemberService members,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>Everyone in the caller's Organization, with the roles they hold.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OrganizationMemberDto>>> List(
        CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return Ok(await members.ListAsync(tenantId, cancellationToken));
    }

    /// <summary>Grants one of the Organization's roles.</summary>
    [HttpPost("{userId:guid}/roles/{roleId:guid}")]
    public Task<IActionResult> AssignRole(Guid userId, Guid roleId, CancellationToken cancellationToken) =>
        RunAsync(tenantId => members.AssignRoleAsync(tenantId, userId, roleId, cancellationToken));

    /// <summary>Takes one of the Organization's roles back.</summary>
    [HttpDelete("{userId:guid}/roles/{roleId:guid}")]
    public Task<IActionResult> UnassignRole(Guid userId, Guid roleId, CancellationToken cancellationToken) =>
        RunAsync(tenantId => members.UnassignRoleAsync(tenantId, userId, roleId, cancellationToken));

    /// <summary>Suspends or restores one person's access without removing them.</summary>
    [HttpPost("{userId:guid}/active")]
    public Task<IActionResult> SetActive(Guid userId, bool active, CancellationToken cancellationToken) =>
        RunAsync(tenantId => members.SetActiveAsync(tenantId, userId, active, cancellationToken));

    /// <summary>Removes somebody from the Organization.</summary>
    [HttpDelete("{userId:guid}")]
    public Task<IActionResult> Remove(Guid userId, CancellationToken cancellationToken) =>
        RunAsync(tenantId => members.RemoveAsync(tenantId, userId, cancellationToken));

    /// <summary>
    /// Resolves the tenant, runs the operation, and turns a refusal into a message the customer sees.
    /// </summary>
    /// <remarks>
    /// <see cref="PermissionGrantException"/> is caught with JumpStart's own wording rather than a
    /// paraphrase, for the same reason <see cref="OrganizationRolesController"/> does: it explains
    /// which of the four rules refused the grant, and a rewrite would only lose that.
    /// </remarks>
    private async Task<IActionResult> RunAsync(Func<Guid, Task> operation)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        try
        {
            await operation(tenantId);
            return NoContent();
        }
        catch (MemberManagementException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (PermissionGrantException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
