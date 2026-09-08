// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <summary>
/// Everything a tenant can do to its own subscription: see where it stands, find out what a change
/// would do, make that change, and call off a change that hasn't happened yet.
/// </summary>
/// <remarks>
/// <para>
/// Sits between the controller and <see cref="PlanChangeCalculator"/>: the calculator decides
/// <em>what</em> a change means (pure arithmetic), this decides whether it's permitted and writes the
/// result down. Keeping those apart is what lets <see cref="QuoteAsync"/> and <see cref="ApplyAsync"/>
/// share one code path - applying is quoting plus persistence, so what a user is shown before
/// accepting cannot disagree with what they get.
/// </para>
/// <para>
/// <strong>No money changes hands here.</strong> Nothing collects payment yet (see
/// <see cref="PlanChangeQuoteDto.AmountDueNow"/>), so an upgrade applies on request and the prorated
/// figure is calculated and shown but never charged or recorded as a debt. When checkout arrives, this
/// is the seam it plugs into: <see cref="ApplyAsync"/> gains a step between "quote is allowed" and
/// "write the new interval".
/// </para>
/// </remarks>
public interface ISubscriptionService
{
    /// <summary>
    /// This tenant's current subscription and any change already queued against it. <c>null</c> when
    /// the tenant has no open subscription interval at all, which shouldn't happen for a
    /// properly-bootstrapped Organization.
    /// </summary>
    Task<SubscriptionDto?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The plans this tenant may choose from - every currently-active plan, cheapest first, each
    /// flagged with whether it's the current one and whether the tenant's server count would fit.
    /// </summary>
    Task<IReadOnlyList<PlanOptionDto>> GetPlanOptionsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every billing period this tenant has been charged for, newest first - one row per billable span,
    /// including the partial ones a mid-period change creates.
    /// </summary>
    Task<IReadOnlyList<BillingHistoryEntryDto>> GetBillingHistoryAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What would happen if this tenant moved to <paramref name="targetPlanId"/> on
    /// <paramref name="targetTermMonths"/> at <paramref name="targetQuantity"/> server slots -
    /// including whether it's allowed at all. Changes nothing.
    /// </summary>
    /// <param name="targetQuantity">
    /// Slots to hold, or <c>null</c> to derive them - see <see cref="ChangePlanRequestDto.Quantity"/>.
    /// </param>
    Task<PlanChangeQuoteDto> QuoteAsync(
        Guid tenantId, Guid targetPlanId, int targetTermMonths, int? targetQuantity = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the change described by <see cref="QuoteAsync"/> and returns that same quote, so the
    /// caller can report exactly what was done. A quote that isn't
    /// <see cref="PlanChangeQuoteDto.Allowed"/> is returned unchanged and nothing is written.
    /// </summary>
    Task<PlanChangeQuoteDto> ApplyAsync(
        Guid tenantId, Guid targetPlanId, int targetTermMonths, int? targetQuantity = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels this tenant's pending scheduled change, leaving the current subscription untouched and
    /// running on as it is. <c>false</c> when there was nothing queued.
    /// </summary>
    Task<bool> CancelScheduledChangeAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
