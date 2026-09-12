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
/// An Organization editing its own name and contact address.
/// </summary>
/// <remarks>
/// The tenant comes from <see cref="ITenantContext"/> - the verified <c>tenant_id</c> claim - and never
/// from the route or body, exactly as in <see cref="OrganizationMembersController"/>, so no request can
/// rename another Organization. <see cref="OrganizationsController"/> is the endpoint that crosses that
/// boundary, and it is gated by a platform permission instead.
/// </remarks>
[ApiController]
[Route("api/organization/settings")]
[Authorize]
[RequirePermission(PermissionCatalog.OrganizationManageSettings)]
public class OrganizationSettingsController(
    IOrganizationSettingsService settings,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>The caller's own Organization name and contact address.</summary>
    [HttpGet]
    public async Task<ActionResult<OrganizationSettingsDto>> Get(CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        var result = await settings.GetAsync(tenantId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Changes them.</summary>
    [HttpPut]
    public async Task<IActionResult> Update(
        [FromBody] UpdateOrganizationSettingsRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Give the organization a name.");
        }

        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return await settings.UpdateAsync(
            tenantId, request.Name.Trim(), request.ContactEmail?.Trim(), cancellationToken)
            ? NoContent()
            : NotFound();
    }
}
