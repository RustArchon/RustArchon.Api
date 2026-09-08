// Copyright ©2026 Scott Blomfield

using System;
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
/// An Organization managing its own roles.
/// </summary>
/// <remarks>
/// <para>
/// The tenant comes from <see cref="ITenantContext"/> - the verified <c>tenant_id</c> claim - and
/// never from the route or body, so there is no request that reaches another Organization's roles.
/// That is what makes this safe to expose to customers, unlike
/// <c>OrganizationsController</c>, which crosses the boundary on purpose and is gated by a platform
/// permission.
/// </para>
/// <para>
/// This is also why <c>RegisterAuthorizationController</c> stays off: JumpStart's generic
/// <c>/api/roles</c> would let a caller name any role id and any permission string, which is exactly
/// what this controller and the service behind it exist to prevent.
/// </para>
/// </remarks>
[ApiController]
[Route("api/organization/roles")]
[Authorize]
public class OrganizationRolesController(
    IOrganizationRoleService roleService,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>
    /// The Organization's roles, and the permissions it may put in them.
    /// </summary>
    /// <remarks>
    /// Readable by anyone who can manage members, not just by whoever can define roles: assigning
    /// somebody a role requires knowing which roles exist.
    /// </remarks>
    [RequirePermission(PermissionCatalog.OrganizationManageMembers)]
    [HttpGet]
    public async Task<ActionResult<OrganizationRolesDto>> List(CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return Ok(await roleService.ListAsync(tenantId, cancellationToken));
    }

    /// <summary>Defines a new role for the Organization.</summary>
    [RequirePermission(PermissionCatalog.OrganizationManageRoles)]
    [HttpPost]
    public async Task<ActionResult<OrganizationRoleDto>> Create(
        [FromBody] SaveRoleRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        try
        {
            return Ok(await roleService.CreateAsync(
                tenantId, request.Name, request.Description, cancellationToken));
        }
        catch (RoleManagementException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Renames one of the Organization's own roles.</summary>
    [RequirePermission(PermissionCatalog.OrganizationManageRoles)]
    [HttpPut("{roleId:guid}")]
    public async Task<ActionResult<OrganizationRoleDto>> Update(
        Guid roleId, [FromBody] SaveRoleRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        try
        {
            return Ok(await roleService.UpdateAsync(
                tenantId, roleId, request.Name, request.Description, cancellationToken));
        }
        catch (RoleManagementException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Retires one of the Organization's own roles.</summary>
    [RequirePermission(PermissionCatalog.OrganizationManageRoles)]
    [HttpDelete("{roleId:guid}")]
    public async Task<IActionResult> Delete(Guid roleId, CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        try
        {
            await roleService.DeleteAsync(tenantId, roleId, cancellationToken);
            return NoContent();
        }
        catch (RoleManagementException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Replaces what a role grants.</summary>
    [RequirePermission(PermissionCatalog.OrganizationManageRoles)]
    [HttpPut("{roleId:guid}/permissions")]
    public async Task<ActionResult<OrganizationRoleDto>> SetPermissions(
        Guid roleId, [FromBody] SetRolePermissionsRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        try
        {
            return Ok(await roleService.SetPermissionsAsync(
                tenantId, roleId, request.Permissions, cancellationToken));
        }
        catch (RoleManagementException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (PermissionGrantException ex)
        {
            // JumpStart's own wording for a refused grant - it explains the refusal better than a
            // paraphrase would, and the four rules are the same ones the customer needs to hear.
            return BadRequest(ex.Message);
        }
    }
}
