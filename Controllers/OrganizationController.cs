// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Creating an Organization of one's own.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Authenticated, but demanding no permission</strong> - and it cannot demand one. The
/// Organization does not exist yet, so there is no tenant to scope a permission to and nothing the
/// caller could be holding. It is the same bootstrap shape as
/// <c>AccountBootstrapController.EnsureTenant</c>, which is why both appear on the
/// authenticated-only-by-design lists the coverage tests keep.
/// </para>
/// <para>
/// What stands in for a permission is that the action is only ever about the caller: they are the
/// founder, they get the Owner role in what they made, and they cannot name anybody else. The one
/// real limit is <see cref="Data.Plan.OnePerOwner"/> - a plan a person may only have one
/// Organization on, which is how the free tier avoids being an unlimited supply of free servers.
/// </para>
/// </remarks>
[ApiController]
[Route("api/organization")]
[Authorize]
public class OrganizationController(
    IOrganizationProvisioningService provisioning,
    ApiDbContext dbContext) : ControllerBase
{
    /// <summary>
    /// Plans this caller could start a new Organization on, and which are closed to them.
    /// </summary>
    /// <remarks>
    /// The already-used ones are returned rather than filtered out, with
    /// <see cref="NewOrganizationPlanDto.AlreadyUsed"/> set, so the screen can show the free tier
    /// greyed out with a reason instead of silently omitting the plan somebody came looking for.
    /// </remarks>
    [HttpGet("plan-options")]
    public async Task<ActionResult<IReadOnlyList<NewOrganizationPlanDto>>> PlanOptions(
        CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        // Read here rather than through IPlanRepository, which has no "all active, with prices" -
        // and prices are not optional: a plan whose Prices collection is empty renders as free.
        var plans = await dbContext.Set<Plan>()
            .Include(p => p.Prices)
            .Where(p => p.Active)
            .ToListAsync(cancellationToken);

        var options = new List<NewOrganizationPlanDto>();

        foreach (var plan in plans.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var used = await provisioning.WouldExceedOnePerOwnerAsync(
                userId, plan.Id, cancellationToken: cancellationToken);

            options.Add(new NewOrganizationPlanDto
            {
                Id = plan.Id,
                Name = plan.Name,
                ColorCode = plan.ColorCode,
                MaximumServers = plan.MaximumServers,
                MaximumUsers = plan.MaximumUsers,
                HasRoles = plan.HasRoles,
                OnePerOwner = plan.OnePerOwner,
                AlreadyUsed = used,
                Prices = [.. plan.Prices.Select(p => new PlanPriceDto
                {
                    TermMonths = p.TermMonths,
                    BaseAmount = p.BaseAmount,
                    IncludedUnits = p.IncludedUnits,
                    UnitAmount = p.UnitAmount,
                    Currency = p.Currency
                })]
            });
        }

        return Ok(options);
    }

    /// <summary>Creates an Organization owned by the caller.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatedOrganizationDto>> Create(
        [FromBody] CreateOrganizationRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Give the organization a name.");
        }

        try
        {
            var tenant = await provisioning.CreateAsync(
                userId, request.Name, request.PlanId, enforceOnePerOwner: true, cancellationToken);

            return Ok(new CreatedOrganizationDto { TenantId = tenant.Id, Name = tenant.Name });
        }
        catch (OrganizationProvisioningException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// The Organizations this caller created, so a screen can say what they already have.
    /// </summary>
    /// <remarks>
    /// Distinct from JumpStart's <c>Tenants.Mine</c>, which lists everything they belong to. This is
    /// the narrower question the one-per-owner rule is counted from.
    /// </remarks>
    [HttpGet("founded")]
    public async Task<ActionResult<IReadOnlyList<FoundedOrganizationDto>>> Founded(
        CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        var founded = await dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.CreatedById == userId)
            .Select(t => new FoundedOrganizationDto
            {
                TenantId = t.Id,
                Name = t.Name,
                CreatedOn = t.CreatedOn,
                PlanName = dbContext.Set<Subscription>()
                    .AcrossAllTenants()
                    .Where(s => s.TenantId == t.Id && s.EndDate == null)
                    .Select(s => s.Plan.Name)
                    .FirstOrDefault()
            })
            .OrderBy(t => t.CreatedOn)
            .ToListAsync(cancellationToken);

        return Ok(founded);
    }

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
