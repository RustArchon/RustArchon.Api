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

    /// <summary>
    /// One Plan with its <see cref="Plan.Prices"/> loaded. Use this rather than the inherited
    /// <c>GetByIdAsync</c> anywhere the plan is about to be priced - an unloaded Prices collection
    /// prices the plan at zero silently instead of failing.
    /// </summary>
    Task<Plan?> GetWithPricesAsync(Guid id);

    /// <summary>How many Organizations have <em>ever</em> been on this specific Plan - every
    /// <see cref="Subscription"/> row referencing it, open or closed. See
    /// <see cref="RustArchon.Shared.DTOs.PlanDto.SubscriberCount"/>'s remarks.</summary>
    /// <remarks>
    /// Counts closed intervals deliberately, and this is the count every "can this Plan still be
    /// changed?" decision uses (edit-in-place vs supersede, and delete). A Plan row is the record of
    /// terms an Organization was actually billed under, so the moment one has been used it stops being
    /// editable - forever, not just while someone is still on it. Narrowing this to currently-open
    /// rows would let an admin retroactively rewrite the price a past subscriber was charged, simply
    /// because they've since moved to a different plan. Use <see cref="Data.Plan"/>'s supersede flow to
    /// change terms going forward instead. (It's also what keeps the delete guard honest: the
    /// <c>Subscription.PlanId</c> foreign key would reject the delete anyway once any row points at it,
    /// so a current-only count would turn a clean refusal into a raw FK violation.)
    /// </remarks>
    Task<int> GetSubscriberCountAsync(Guid planId);

    /// <summary>
    /// Deactivates every currently-active Plan with <paramref name="name"/> except
    /// <paramref name="excludePlanId"/> (if given) - the mechanism behind "at most one active Plan per
    /// Name" (see <see cref="Data.Plan"/>'s remarks). Called by every write path that's about to
    /// persist a Plan with <c>Active: true</c>, before that save happens.
    /// </summary>
    Task DeactivateOtherActiveAsync(string name, Guid? excludePlanId);

    /// <summary>All Plan rows, ordered by Name then newest first - the admin page sees every
    /// historical version, not just active ones. Prices are eagerly loaded.</summary>
    Task<List<Plan>> GetAllOrderedAsync();

    /// <summary>
    /// Replaces this Plan's price rows wholesale with <paramref name="prices"/>.
    /// </summary>
    /// <remarks>
    /// Delete-then-insert rather than a merge, because a plan's set of terms is itself editable: an
    /// admin removing the quarterly option has to actually lose that row, and a merge would leave it
    /// behind. Only ever called for a plan nobody has been on - an edit to a used plan goes through the
    /// supersede flow instead, which creates a new Plan with its own prices rather than touching these.
    /// </remarks>
    Task ReplacePricesAsync(Guid planId, IEnumerable<PlanPrice> prices);
}
