// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// A plan/term change a tenant has accepted that takes effect on a future date rather than now - the
/// deferred half of a plan change.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a change that costs the tenant <em>less</em> never takes effect immediately: they've
/// already paid through the end of the current term and downgrades earn no refund, so the reduction
/// waits until the term they paid for is actually over. That's true of a smaller plan and of a shorter
/// term alike, and a single request can defer one while applying the other right away.
/// </para>
/// <para>
/// This is a queue of intent, not history - it records what <em>will</em> happen. The record of what
/// <em>did</em> happen is <see cref="Subscription"/>, which gains a new interval when
/// <see cref="Infrastructure.SubscriptionScheduleService"/> applies one of these. Rows are kept after
/// they're applied or cancelled (hence the two nullable timestamps rather than deleting them) so
/// "why did my plan change last Tuesday?" has an answer.
/// </para>
/// <para>
/// <strong>At most one pending row per tenant</strong>, enforced by a partial unique index on
/// <c>(TenantId) WHERE "AppliedOn" IS NULL AND "CancelledOn" IS NULL</c> (see
/// <c>ApiDbContext.OnModelCreating</c>, the same technique <see cref="Plan"/> and
/// <see cref="Subscription"/> both use). Requesting a new change supersedes whatever was queued -
/// cancelling the old row rather than stacking a second one, so there's never a question of which
/// queued change wins.
/// </para>
/// <para>
/// Carries the <strong>whole target state</strong> (both <see cref="PlanId"/> and <see cref="Term"/>),
/// not just the dimension that was deferred. An applier shouldn't have to reconstruct what the tenant
/// asked for by diffing against whatever their subscription happens to look like months later.
/// </para>
/// </remarks>
[Table("ScheduledPlanChange")]
public class ScheduledPlanChange : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>The Plan the tenant will be on once this is applied.</summary>
    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;

    /// <summary>The term, in months, the tenant will be billed on once this is applied.</summary>
    public int TermMonths { get; set; } = BillingTerms.Monthly;

    /// <summary>
    /// The capacity the tenant will hold once this is applied - see <see cref="SubscriptionPeriod.Quantity"/>.
    /// </summary>
    /// <remarks>
    /// Releasing capacity is a reduction, so like a plan downgrade or a shorter term it waits for the end
    /// of the period and lands here. The scheduler has to re-check it on the way in: slots can't be
    /// released below the number of servers actually running, and servers can be added between accepting
    /// this and it taking effect.
    /// </remarks>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// When this takes effect - always the end of the billing period that was in force when it was
    /// accepted, <em>including</em> any extension applied in the same request. Extending a term and
    /// downgrading a plan at once pushes the downgrade out to the end of the new, longer term; that's
    /// a consequence users are shown before accepting, which is why the date is fixed here at
    /// acceptance time rather than recomputed later.
    /// </summary>
    public DateTimeOffset EffectiveDate { get; set; }

    public DateTimeOffset CreatedOn { get; set; }

    /// <summary>Set once this has been turned into a real <see cref="Subscription"/> interval.</summary>
    public DateTimeOffset? AppliedOn { get; set; }

    /// <summary>Set when the tenant cancelled this, or when a newer request superseded it.</summary>
    public DateTimeOffset? CancelledOn { get; set; }

    /// <summary>Pending means not yet applied and not cancelled - the state the partial unique index
    /// constrains to one per tenant.</summary>
    [NotMapped]
    public bool IsPending => AppliedOn is null && CancelledOn is null;
}
