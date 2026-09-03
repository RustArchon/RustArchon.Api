// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
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
    /// Plan has no subscribers yet. If <see cref="CreatePlanDto.Active"/> is <c>true</c>, any other
    /// currently-active Plan with the same Name is deactivated first.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PlanDto>> Create([FromBody] CreatePlanDto createDto)
    {
        if (createDto.Active)
        {
            await _repository.DeactivateOtherActiveAsync(createDto.Name, excludePlanId: null);
        }

        var entity = _mapper.Map<Plan>(createDto);
        var created = await _repository.AddAsync(entity);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, await ToDtoAsync(created));
    }

    /// <summary>
    /// Edits an existing Plan in place. Only meant to be called when
    /// <see cref="PlanDto.SubscriberCount"/> is zero - the admin page routes to <see cref="Supersede"/>
    /// instead once any Organization is assigned, but this endpoint itself doesn't block on it (an
    /// admin fixing a genuine data-entry mistake on an already-subscribed Plan is still a legitimate,
    /// if unusual, thing to do). If <see cref="UpdatePlanDto.Active"/> is <c>true</c>, any other
    /// currently-active Plan with the same Name is deactivated first.
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
        return Ok(await ToDtoAsync(updated));
    }

    /// <summary>
    /// Supersedes a Plan that already has subscribers: creates a new Plan row (same Name as the one
    /// being superseded, the given terms/color, <c>Active: true</c>), then deactivates the old one
    /// along with any other currently-active Plan with that Name. The old row is left in place,
    /// untouched otherwise - existing Organizations stay pointed at it (see
    /// <see cref="TenantPlan"/>'s remarks), so this never changes what a current subscriber is paying
    /// or entitled to.
    /// </summary>
    [HttpPost("{id}/supersede")]
    public async Task<ActionResult<PlanDto>> Supersede(Guid id, [FromBody] SupersedePlanDto supersedeDto)
    {
        var oldPlan = await _repository.GetByIdAsync(id, null);
        if (oldPlan == null)
        {
            return NotFound();
        }

        var newPlan = await _repository.AddAsync(new Plan
        {
            Name = oldPlan.Name,
            ColorCode = supersedeDto.ColorCode,
            MonthlyPrice = supersedeDto.MonthlyPrice,
            QuarterlyPrice = supersedeDto.QuarterlyPrice,
            AnnualPrice = supersedeDto.AnnualPrice,
            RetentionHistory = supersedeDto.RetentionHistory,
            HasRoles = supersedeDto.HasRoles,
            MaximumServers = supersedeDto.MaximumServers,
            MaximumUsers = supersedeDto.MaximumUsers,
            Active = true
        });

        // Excludes the row we just created - deactivates oldPlan (and, defensively, anything else with
        // this Name that was somehow also still active).
        await _repository.DeactivateOtherActiveAsync(oldPlan.Name, excludePlanId: newPlan.Id);

        return Ok(await ToDtoAsync(newPlan));
    }

    /// <summary>
    /// Permanently removes a Plan created by mistake. Refuses to delete one with any subscribers -
    /// <see cref="TenantPlan"/> rows reference it by <c>PlanId</c>, and deleting out from under a
    /// current Organization would leave it planless, which is never a supported state. Deactivate it
    /// (via <see cref="Update"/>) instead if it just shouldn't be offered to new sign-ups anymore.
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
            return Conflict($"This plan has {subscriberCount} organization(s) on it and can't be deleted. Deactivate it instead.");
        }

        await _repository.DeleteAsync(id);
        return NoContent();
    }
}
