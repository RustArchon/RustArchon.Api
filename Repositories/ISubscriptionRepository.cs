// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="Subscription"/> - a tenant's plan history, one row per plan they
/// have been on (see <see cref="Subscription"/>'s remarks). Billing periods are
/// <see cref="SubscriptionPeriod"/> rows hanging off these.
/// </summary>
public interface ISubscriptionRepository : IRepository<Subscription>
{
    /// <summary>This tenant's <em>current</em> plan - the single open row (null
    /// <see cref="Data.Subscription.EndDate"/>), with <see cref="Data.Subscription.Plan"/> eagerly loaded.
    /// Every Organization has exactly one from the moment it's created (see
    /// <see cref="Data.Subscription"/>'s remarks), so <c>null</c> here means account bootstrap either
    /// hasn't run yet or failed partway through - not that their plan expired.</summary>
    Task<Subscription?> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>This tenant's plan history, newest first, each with its Plan loaded.</summary>
    Task<IReadOnlyList<Subscription>> GetHistoryForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The billing period slice currently being charged - the most recently started
    /// <see cref="SubscriptionPeriod"/> across all of this tenant's plans, with its
    /// <see cref="SubscriptionPeriod.Subscription"/> and that plan's <see cref="Plan"/> loaded.
    /// </summary>
    /// <remarks>
    /// Identified as "most recently started" rather than by an open-ended date, because a slice always
    /// knows when it ends: its <see cref="SubscriptionPeriod.EndDate"/> is a real renewal date set when the
    /// row is created, not a null waiting to be filled. Slices are contiguous and never overlap, so the
    /// latest one is unambiguously the live one.
    /// </remarks>
    Task<SubscriptionPeriod?> GetCurrentTermAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every billing slice this tenant has been charged for, newest first, with plan details loaded -
    /// the billing history a "what have I paid?" screen renders directly.
    /// </summary>
    Task<IReadOnlyList<SubscriptionPeriod>> GetBillingHistoryAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
