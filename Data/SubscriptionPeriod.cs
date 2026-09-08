// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// One billable span of a subscription - what a tenant earned service for, over which dates, under
/// which <see cref="Subscription"/>. The revenue-recognition record.
/// </summary>
/// <remarks>
/// <para>
/// Was <c>TenantPlanTerm</c>. Split out from <see cref="Subscription"/> because the two answer different
/// questions and move on different schedules. <see cref="Subscription"/> says <em>which plan, since
/// when</em> - written once when a tenant moves onto a plan and closed only when they leave it. This
/// says <em>what was earned, covering what dates</em> - a new row every renewal, and another whenever a
/// change lands mid-period.
/// </para>
/// <para>
/// The earlier design folded both into <see cref="Subscription"/>, with period fields that renewal
/// overwrote in place. That kept a long-lived subscription to a single row, but it destroyed every
/// period it rolled past: a tenant two years into a monthly plan had one row and no record that the
/// preceding twenty-three periods existed, so "show me my billing history" had no answer to give.
/// Renewal is an insert here, never an update, and nothing is ever overwritten.
/// </para>
/// <para>
/// <strong>Earned, not billed.</strong> This records revenue <em>earned</em>; an invoice records money
/// <em>asked for</em>. Those are different dates and different amounts - which is what lets a tenant be
/// billed annually while revenue is recognised monthly - so the two live in different tables and meet
/// only where an invoice line points back at a period. The <c>BilledOn</c> and <c>PaidOn</c> stamps this
/// class used to carry were the beginnings of collapsing that distinction: issuing is a property of a
/// document rather than of one line on it, and a single timestamp can express neither partial payment,
/// nor several payments, nor a refund reversing one.
/// </para>
/// <para>
/// <strong>Two date ranges, deliberately.</strong> <see cref="StartDate"/>/<see cref="EndDate"/> are
/// the span this row covers; <see cref="PeriodStart"/>/<see cref="PeriodEnd"/> are the billing
/// period it belongs to. They're equal for an untouched period, and diverge when a change lands
/// mid-period: the open row is closed at the change instant and a new one opened, both carrying the
/// same period. Renewal is what starts a new period.
/// </para>
/// <para>
/// The period has to be stored rather than inferred from the slice, because proration measures against
/// it and the renewal date is derived from it. Re-anchoring each change to its own slice instead would
/// walk the renewal date forward every time: a same-term upgrade on January 21 would move renewal from
/// February 1 to February 11, and a tenant who upgraded repeatedly could push their renewal out
/// indefinitely without ever paying for the extra time.
/// </para>
/// </remarks>
[Table("SubscriptionPeriod")]
public class SubscriptionPeriod : Entity
{
    public Guid SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = null!;

    /// <summary>How many months the billing period this row belongs to runs for.</summary>
    public int TermMonths { get; set; } = BillingTerms.Monthly;

    /// <summary>
    /// How much capacity this span was billed for - the number of server slots the tenant had bought.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third dimension of a subscription, alongside plan and term, and it behaves like the other two:
    /// buying more slots costs more and so applies immediately and prorated; releasing slots costs less
    /// and so waits for the end of the period, with no refund. Persisted per span rather than read off
    /// the tenant's current server count, for the same reason <see cref="EarnedAmount"/> is: this is the
    /// record of what was charged, and capacity changes.
    /// </para>
    /// <para>
    /// <strong>Entitlement is bought, not implied by adding a server.</strong> Servers consume slots that
    /// already exist - <c>RustServersController.Create</c> checks against this, not against
    /// <see cref="Plan.MaximumServers"/>. That makes one purchase one charge however many slots it buys,
    /// and stops a charge being the surprise consequence of a UI action taken for another reason.
    /// </para>
    /// <para>
    /// On a flat-tier plan this is pinned to the price row's <see cref="PlanPrice.IncludedUnits"/> and
    /// isn't user-editable: <see cref="PlanPrice.UnitAmount"/> is zero there, so buying slots would
    /// charge nothing and mean nothing.
    /// </para>
    /// </remarks>
    public int Quantity { get; set; } = 1;

    /// <summary>Start of the billing period this row belongs to - the anchor proration measures
    /// elapsed and remaining days against. Shared by every slice of the same period.</summary>
    public DateTimeOffset PeriodStart { get; set; }

    /// <summary>End of the billing period this row belongs to: the renewal date. Shared by every slice
    /// of the same period.</summary>
    public DateTimeOffset PeriodEnd { get; set; }

    /// <summary>Start of the span this row actually covers. Equal to <see cref="PeriodStart"/>
    /// unless an earlier change already split this period.</summary>
    public DateTimeOffset StartDate { get; set; }

    /// <summary>
    /// End of the span this row covers. Equal to <see cref="PeriodEnd"/> until a change lands
    /// mid-period, which closes this row at the change instant and opens the next.
    /// </summary>
    public DateTimeOffset EndDate { get; set; }

    /// <summary>
    /// What this span is worth - the revenue-recognition basis, not the bill.
    /// </summary>
    /// <remarks>
    /// Named against <c>InvoiceLine.Amount</c>, which is what a tenant was actually asked to pay.
    /// Persisted rather than recomputed on read: the prices behind it move - a <see cref="Plan"/> can be
    /// superseded, and a mid-period change earns a prorated fraction that no list price equals.
    /// </remarks>
    [Column(TypeName = "numeric(18,2)")]
    public decimal EarnedAmount { get; set; }

    /// <summary>True when this row covers the whole of its period rather than a slice of it.</summary>
    [NotMapped]
    public bool IsWholePeriod => StartDate == PeriodStart && EndDate == PeriodEnd;
}
