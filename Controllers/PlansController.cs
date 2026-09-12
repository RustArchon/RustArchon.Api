// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin management of pricing Plans: listing every historical version, creating new ones,
/// editing an unsubscribed one in place, superseding a subscribed one, and deleting a mistake. See
/// <see cref="PublicPlansController"/> for the anonymous side (RustArchon.Web's pricing page).
/// </summary>
/// <remarks>
/// Gated by the <c>ManagePlans</c> authorization policy, same mechanism/reasoning as
/// <see cref="InvitationCodesController"/> and <see cref="PlatformSettingsController"/> - Plans aren't
/// tenant-scoped, so <c>[EntityAuthorize]</c> (which resolves against a tenant-scoped <c>Role</c>)
/// doesn't apply here either. Hand-written rather than an
/// <see cref="JumpStart.Api.Controllers.ApiControllerBase{TEntity,TDto,TCreateDto,TUpdateDto,TRepository}"/>
/// subclass for the same reason those two are.
/// </remarks>
[ApiController]
[Route("api/plans")]
[Authorize(Policy = "ManagePlans")]
public class PlansController : ControllerBase
{
    private readonly IPlanRepository _repository;
    private readonly IMapper _mapper;

    public PlansController(IPlanRepository repository, IMapper mapper)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    private async Task<PlanDto> ToDtoAsync(Plan plan)
    {
        var dto = _mapper.Map<PlanDto>(plan);
        dto.SubscriberCount = await _repository.GetSubscriberCountAsync(plan.Id);
        return dto;
    }

    /// <summary>
    /// Turns the price rows a client sent into entities, de-duplicated by term.
    /// </summary>
    /// <remarks>
    /// The de-duplication matters: a unique index enforces one price per (plan, term), and a form that
    /// submitted the same term twice would otherwise fail at the database with a constraint violation
    /// rather than a message anyone can act on. Last one wins, which is what a user editing a row twice
    /// would expect.
    /// </remarks>
    private static List<PlanPrice> ToPrices(IEnumerable<PlanPriceDto> prices) =>
        prices
            .GroupBy(p => p.TermMonths)
            .Select(g => g.Last())
            .Select(p => new PlanPrice
            {
                TermMonths = p.TermMonths,
                BaseAmount = p.BaseAmount,
                IncludedUnits = p.IncludedUnits,
                UnitAmount = p.UnitAmount,
                Currency = string.IsNullOrWhiteSpace(p.Currency) ? "USD" : p.Currency.ToUpperInvariant()
            })
            .ToList();

    [HttpGet("{id}")]
    public async Task<ActionResult<PlanDto>> GetById(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity == null)
        {
            return NotFound();
        }

        return Ok(await ToDtoAsync(entity));
    }

    /// <summary>Every historical Plan row, grouped by Name then newest first - not just active ones.</summary>
    [HttpGet]
    public async Task<ActionResult<List<PlanDto>>> GetAll()
    {
        var plans = await _repository.GetAllOrderedAsync();
        var dtos = new List<PlanDto>(plans.Count);
        foreach (var plan in plans)
        {
            dtos.Add(await ToDtoAsync(plan));
        }

        return Ok(dtos);
    }

    /// <summary>
    /// Creates a new Plan row - either a brand-new Name, or a fresh draft for a Name whose current
    /// Plan has never been used. If <see cref="CreatePlanDto.Active"/> is <c>true</c>, any other
    /// currently-active Plan with the same Name is deactivated first.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PlanDto>> Create([FromBody] CreatePlanDto createDto)
    {
        if (createDto.Active)
        {
            await _repository.DeactivateOtherActiveAsync(createDto.Name, excludePlanId: null);
        }

        // Prices are set here rather than by the mapper - see PlanMappingProfile for why that map is
        // ignored, and ToPrices for the de-duplication the unique index depends on.
        var entity = _mapper.Map<Plan>(createDto);
        entity.Prices = ToPrices(createDto.Prices);

        var created = await _repository.AddAsync(entity);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, await ToDtoAsync(created));
    }

    /// <summary>
    /// Edits an existing Plan in place. Only meant to be called when
    /// <see cref="PlanDto.SubscriberCount"/> is zero - i.e. no Organization has ever been on this
    /// Plan, not merely none right now. Once one has, its terms are the record of what somebody was
    /// billed, and the admin page routes to <see cref="Supersede"/> instead. This endpoint itself
    /// doesn't block on it (an admin fixing a genuine data-entry mistake on an already-used Plan is
    /// still a legitimate, if unusual, thing to do). If <see cref="UpdatePlanDto.Active"/> is
    /// <c>true</c>, any other currently-active Plan with the same Name is deactivated first.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<PlanDto>> Update(Guid id, [FromBody] UpdatePlanDto updateDto)
    {
        if (!id.Equals(updateDto.Id))
        {
            return BadRequest("ID mismatch");
        }

        var entity = await _repository.GetByIdAsync(id, null);
        if (entity == null)
        {
            return NotFound();
        }

        if (updateDto.Active)
        {
            await _repository.DeactivateOtherActiveAsync(entity.Name, excludePlanId: id);
        }

        _mapper.Map(updateDto, entity);
        var updated = await _repository.UpdateAsync(entity);

        // Replaced separately: UpdateAsync copies scalar values onto the tracked entity and doesn't
        // touch child collections, so removing a term would otherwise silently do nothing.
        await _repository.ReplacePricesAsync(id, ToPrices(updateDto.Prices));

        var refreshed = await _repository.GetByIdAsync(id, null) ?? updated;
        return Ok(await ToDtoAsync(refreshed));
    }

    /// <summary>
    /// Supersedes a Plan that has already been used: deactivates the old one (and, defensively,
    /// anything else with that Name that was somehow also still active), then creates a new Plan row
    /// (same Name, the given terms/color, <c>Active: true</c>). The old row is left in place,
    /// untouched otherwise - current Organizations stay pointed at it and past
    /// <see cref="Subscription"/> intervals keep resolving to the terms that were actually in force at
    /// the time (see <see cref="Subscription"/>'s remarks), so this changes neither what a current
    /// subscriber is paying nor what a historical record says they paid.
    /// </summary>
    /// <remarks>
    /// Deactivate-then-insert, not the other way around: the partial unique index
    /// <c>IX_Plan_Name_WhereActive</c> allows at most one active row per Name at any moment, so
    /// inserting the new row while the old one is still active fails the insert outright with a
    /// duplicate-key violation - confirmed live, not theoretical.
    /// </remarks>
    [HttpPost("{id}/supersede")]
    public async Task<ActionResult<PlanDto>> Supersede(Guid id, [FromBody] SupersedePlanDto supersedeDto)
    {
        var oldPlan = await _repository.GetByIdAsync(id, null);
        if (oldPlan == null)
        {
            return NotFound();
        }

        await _repository.DeactivateOtherActiveAsync(oldPlan.Name, excludePlanId: null);

        // The new row gets its own PlanPrice rows rather than sharing the old plan's: prices are what a
        // subscriber signed up under, so the superseded plan has to keep its own copy untouched.
        var newPlan = await _repository.AddAsync(new Plan
        {
            Name = oldPlan.Name,
            ColorCode = supersedeDto.ColorCode,
            PricingModel = supersedeDto.PricingModel,
            RetentionHistory = supersedeDto.RetentionHistory,
            HasRoles = supersedeDto.HasRoles,
            OnePerOwner = supersedeDto.OnePerOwner,
            MaximumServers = supersedeDto.MaximumServers,
            MaximumUsers = supersedeDto.MaximumUsers,
            Active = true,
            Prices = ToPrices(supersedeDto.Prices)
        });

        return Ok(await ToDtoAsync(newPlan));
    }

    /// <summary>
    /// Permanently removes a Plan created by mistake - only one no Organization has ever been on.
    /// Deleting a Plan out from under a current Organization would leave it planless (never a
    /// supported state), and deleting one that only *past* intervals reference would tear a hole in
    /// those Organizations' subscription history - so <see cref="Subscription"/> rows of either kind
    /// block it. Deactivate it (via <see cref="Update"/>) instead if it just shouldn't be offered to
    /// new sign-ups anymore.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity == null)
        {
            return NotFound();
        }

        var subscriberCount = await _repository.GetSubscriberCountAsync(id);
        if (subscriberCount > 0)
        {
            return Conflict($"This plan has been used by {subscriberCount} organization(s) and can't be deleted. Deactivate it instead.");
        }

        await _repository.DeleteAsync(id);
        return NoContent();
    }
}
