// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Administration;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The site-admin view of individual customers - every Organization on the platform, and the support
/// actions that can be taken on one.
/// </summary>
/// <remarks>
/// <para>
/// Behind <c>ManageOrganizations</c>, which is its own permission rather than a reuse of
/// <c>ViewReports</c>. Reading aggregate figures about the customer base and opening up one named
/// customer's account - their servers, their invoices, who belongs to it - are different levels of
/// access, and an analyst who should see the second is not automatically someone who should see the
/// first.
/// </para>
/// <para>
/// Routed under <c>api/admin/</c> so the cross-tenant surface is one greppable prefix rather than
/// scattered admin actions on the tenant-scoped controllers. Everything below deliberately crosses the
/// tenant boundary; nothing else in the Api does, outside the reports.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/organizations")]
[Authorize(Policy = "ManageOrganizations")]
public class OrganizationsController(
    IOrganizationAdminService organizations,
    IOrganizationServerAdminService servers,
    IAccessAdminService access,
    IOrganizationLifecycleService lifecycle) : ControllerBase
{
    /// <summary>Organizations matching the filter, newest sign-up first.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OrganizationSummaryDto>>> List(
        [FromQuery] string? search,
        [FromQuery] Guid? planId,
        [FromQuery] SubscriptionStatus? status,
        [FromQuery] bool includeCancelled = false,
        [FromQuery] decimal minimumOutstanding = 0m,
        CancellationToken cancellationToken = default)
    {
        var results = await organizations.ListAsync(
            new OrganizationQueryDto
            {
                Search = search,
                PlanId = planId,
                Status = status,
                IncludeCancelled = includeCancelled,
                MinimumOutstanding = minimumOutstanding
            },
            cancellationToken);

        return Ok(results);
    }

    /// <summary>Everything behind one Organization.</summary>
    [HttpGet("{tenantId:guid}")]
    public async Task<ActionResult<OrganizationDetailDto>> Get(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var detail = await organizations.GetAsync(tenantId, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>The plans available to filter by, or to move an Organization onto.</summary>
    [HttpGet("plan-options")]
    public async Task<ActionResult<IReadOnlyList<ReportFilterOptionDto>>> GetPlanOptions(
        CancellationToken cancellationToken) =>
        Ok(await organizations.GetPlanOptionsAsync(cancellationToken));

    /// <summary>Enables one of the Organization's servers and asks a worker to claim it.</summary>
    [HttpPost("{tenantId:guid}/servers/{serverId:guid}/enable")]
    public async Task<IActionResult> EnableServer(
        Guid tenantId, Guid serverId, CancellationToken cancellationToken) =>
        await servers.EnableAsync(tenantId, serverId, cancellationToken) ? NoContent() : NotFound();

    /// <summary>Disables one of the Organization's servers, tearing its connection down.</summary>
    [HttpPost("{tenantId:guid}/servers/{serverId:guid}/disable")]
    public async Task<IActionResult> DisableServer(
        Guid tenantId, Guid serverId, CancellationToken cancellationToken) =>
        await servers.DisableAsync(tenantId, serverId, cancellationToken) ? NoContent() : NotFound();

    /// <summary>Corrects a server's host, port or RCON password on the Organization's behalf.</summary>
    [HttpPut("{tenantId:guid}/servers/{serverId:guid}/connection")]
    public async Task<IActionResult> UpdateServerConnection(
        Guid tenantId, Guid serverId,
        [FromBody] UpdateServerConnectionRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Host) || request.Port is < 1 or > 65535)
        {
            return BadRequest("A host and a port between 1 and 65535 are required.");
        }

        return await servers.UpdateConnectionAsync(
            tenantId, serverId, request.Host.Trim(), request.Port, request.RconPassword, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    /// <summary>Sends an RCON command to one of the Organization's servers.</summary>
    /// <returns>
    /// 200 with the result, 409 when the server has no live connection, or 404 when no such server
    /// belongs to that Organization.
    /// </returns>
    [HttpPost("{tenantId:guid}/servers/{serverId:guid}/command")]
    public async Task<ActionResult<RconCommandResult>> SendServerCommand(
        Guid tenantId, Guid serverId,
        [FromBody] SendServerCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return BadRequest("A command is required.");
        }

        var result = await servers.SendCommandAsync(
            tenantId, serverId, request.Command, cancellationToken);

        if (result is null)
        {
            return NotFound();
        }

        return !result.Success && result.Error == "NotConnected" ? Conflict(result) : Ok(result);
    }

    /// <summary>Adds somebody to the Organization, optionally with a role.</summary>
    [HttpPost("{tenantId:guid}/members")]
    public async Task<IActionResult> AddMember(
        Guid tenantId, [FromBody] AddMemberRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UserId == Guid.Empty)
        {
            return BadRequest("A user is required.");
        }

        return await access.AddMemberAsync(tenantId, request.UserId, request.RoleId, cancellationToken)
            ? NoContent()
            : Conflict("That user already belongs to this organization.");
    }

    /// <summary>Removes somebody from the Organization, along with their roles there.</summary>
    [HttpDelete("{tenantId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(
        Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await access.RemoveMemberAsync(tenantId, userId, cancellationToken) ? NoContent() : NotFound();

    /// <summary>Suspends or restores one person's access without removing them.</summary>
    [HttpPost("{tenantId:guid}/members/{userId:guid}/active")]
    public async Task<IActionResult> SetMemberActive(
        Guid tenantId, Guid userId, [FromQuery] bool active, CancellationToken cancellationToken) =>
        await access.SetMemberActiveAsync(tenantId, userId, active, cancellationToken)
            ? NoContent()
            : NotFound();

    /// <summary>Grants one of the Organization's roles to a member.</summary>
    [HttpPost("{tenantId:guid}/members/{userId:guid}/roles/{roleId:guid}")]
    public async Task<IActionResult> AssignRole(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken) =>
        await access.AssignRoleAsync(tenantId, userId, roleId, cancellationToken) ? NoContent() : NotFound();

    /// <summary>Takes one of the Organization's roles back.</summary>
    [HttpDelete("{tenantId:guid}/members/{userId:guid}/roles/{roleId:guid}")]
    public async Task<IActionResult> UnassignRole(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken) =>
        await access.UnassignRoleAsync(tenantId, userId, roleId, cancellationToken) ? NoContent() : NotFound();

    /// <summary>
    /// Moves the Organization between Active, PastDue and Suspended - see
    /// <see cref="IOrganizationLifecycleService"/>. Suspending stops their servers.
    /// </summary>
    [HttpPost("{tenantId:guid}/status")]
    public async Task<IActionResult> SetStatus(
        Guid tenantId, [FromBody] SetOrganizationStatusRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Status == SubscriptionStatus.Cancelled)
        {
            return BadRequest("Use the cancel action to end an organization.");
        }

        // Suspension takes somebody's servers offline. Requiring a reason is not paperwork - it is the
        // only thing that answers "why is this account off?" three weeks later.
        if (request.Status == SubscriptionStatus.Suspended && string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest("A reason is required when suspending an organization.");
        }

        return await lifecycle.SetStatusAsync(tenantId, request.Status, request.Reason?.Trim(), cancellationToken)
            ? NoContent()
            : NotFound();
    }

    /// <summary>
    /// Ends the Organization: closes its subscription, stops its servers, retires the tenant, and
    /// notifies its contact address - which template depends on <see cref="CancelOrganizationRequestDto.Category"/>.
    /// </summary>
    [HttpPost("{tenantId:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid tenantId, [FromBody] CancelOrganizationRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await lifecycle.CancelAsync(tenantId, request.Category, request.Reason?.Trim(), cancellationToken)
            ? NoContent()
            : NotFound();
    }

    /// <summary>Brings a cancelled Organization back on a chosen plan.</summary>
    [HttpPost("{tenantId:guid}/reopen")]
    public async Task<IActionResult> Reopen(
        Guid tenantId, [FromBody] ReopenOrganizationRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PlanId == Guid.Empty)
        {
            return BadRequest("A plan is required.");
        }

        return await lifecycle.ReopenAsync(tenantId, request.PlanId, request.TermMonths, cancellationToken)
            ? NoContent()
            : Conflict("That organization could not be reopened - it may already have an open subscription.");
    }
}
