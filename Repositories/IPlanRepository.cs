// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="Plan"/> entities.
/// </summary>
public interface IPlanRepository : IRepository<Plan>
{
    /// <summary>
    /// The cheapest currently-active Plan (by <see cref="Plan.MonthlyPrice"/>, ties broken by the
    /// oldest <c>CreatedOn</c>), used to assign a brand-new Organization its starting Plan - see
    /// <c>AccountBootstrapController</c>. <c>null</c> only if no Plan is active at all, which should
    /// never happen on a properly-seeded deployment.
    /// </summary>
    Task<Plan?> GetCheapestActiveAsync();

    /// <summary>How many Organizations (<see cref="TenantPlan"/> rows) are currently assigned to this
    /// specific Plan - see <see cref="RustArchon.Shared.DTOs.PlanDto.SubscriberCount"/>'s remarks.</summary>
    Task<int> GetSubscriberCountAsync(Guid planId);

    /// <summary>
    /// Deactivates every currently-active Plan with <paramref name="name"/> except
    /// <paramref name="excludePlanId"/> (if given) - the mechanism behind "at most one active Plan per
    /// Name" (see <see cref="Data.Plan"/>'s remarks). Called by every write path that's about to
    /// persist a Plan with <c>Active: true</c>, before that save happens.
    /// </summary>
    Task DeactivateOtherActiveAsync(string name, Guid? excludePlanId);

    /// <summary>All Plan rows, ordered by Name then newest first - the admin page sees every
    /// historical version, not just active ones.</summary>
    Task<List<Plan>> GetAllOrderedAsync();
}
