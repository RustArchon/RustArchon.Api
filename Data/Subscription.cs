// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// One interval of a Tenant's ("Organization's") subscription history - which <see cref="Plan"/> it
/// was on, from <see cref="StartDate"/> until <see cref="EndDate"/>. The single row with a null
/// <see cref="EndDate"/> is the tenant's current subscription.
/// </summary>
/// <remarks>
/// <para>
/// Was <c>TenantPlan</c>. Renamed - same rows, same meaning - as part of separating the three concerns
/// the billing subsystem is built around: the <strong>catalog</strong> says what things cost, the
/// <strong>subscription</strong> says what a tenant is entitled to and when they earned it, and
/// <strong>billing</strong> says what was asked for and what came back. The old name described the join
/// rather than the thing, and the thing is a subscription.
/// </para>
/// <para>
/// A dedicated table rather than a <c>PlanId</c> column added directly onto JumpStart's own
/// <see cref="Tenant"/> class - RustArchon doesn't fork or subclass JumpStart's framework entities
/// (see <c>ApiDbContext.OnModelCreating</c>'s remarks on <see cref="Tenant.Settings"/> for the same
/// reasoning applied elsewhere), and every other RustArchon-owned entity that relates to a tenant
/// already follows the "reference <c>TenantId</c>, don't touch <c>Tenant</c> itself" pattern (see
/// <see cref="RustServer"/>).
/// </para>
/// <para>
/// <strong>This is history, not a current-state join table.</strong> It was originally the latter -
/// exactly one row per tenant, enforced by a plain unique index on <see cref="TenantId"/> - which
/// answered "what plan is this tenant on?" but destroyed the answer to "what were they on before, and
/// when did that change?" every time a plan changed. Plans do change (upgrades, downgrades,
/// promotional pricing), and knowing when and to what is needed for billing, support and revenue
/// reporting, so a change now <em>closes</em> the open row and <em>opens</em> a new one instead of
/// overwriting anything. Nothing here is ever mutated except an open row's <see cref="EndDate"/> and
/// <see cref="Status"/>, and no row is ever deleted.
/// </para>
/// <para>
/// <strong>Plan changes only.</strong> A row spans everything from moving onto a plan until leaving it,
/// however many billing periods that covers - so "how long have they been on this plan?" is one
/// subtraction. Billing periods live in <see cref="SubscriptionPeriod"/>, one row each, and renewal
/// never touches this table. An earlier design folded periods in here and had renewal overwrite them in
/// place, which meant a tenant two years into an unchanged plan had one row and no record of the
/// twenty-three periods it had rolled past; separating the two keeps both timelines whole.
/// </para>
/// <para>
/// <strong>The invariant</strong> is "at most one <em>open</em> row per tenant", not "one row per
/// tenant" - enforced by a partial unique index on <see cref="TenantId"/> filtered to
/// <c>EndDate IS NULL</c> (see <c>ApiDbContext.OnModelCreating</c>, the same technique
/// <see cref="Plan"/> uses for its one-active-per-Name rule). Any number of closed rows per tenant is
/// normal. Every Organization gets its first open row the moment it's created
/// (<c>AccountBootstrapController.EnsureTenant</c>), and
/// <see cref="Infrastructure.SubscriptionBackfiller"/> closes that gap for any tenant that predates it -
/// there is no supported "no current subscription" state.
/// </para>
/// <para>
/// Intervals are contiguous, not merely ordered: a change stamps the closing row's
/// <see cref="EndDate"/> and the opening row's <see cref="StartDate"/> with the same instant, so the
/// history has no gaps and "which plan was this tenant on at time T" has exactly one answer for every T
/// since sign-up. Treat an interval as half-open (<c>StartDate &lt;= T &lt; EndDate</c>) so the
/// changeover instant belongs to the new plan only.
/// </para>
/// <para>
/// Deliberately points at a specific <see cref="Plan.Id"/>, not just a <see cref="Plan.Name"/>: when a
/// subscribed-to Plan is superseded (see <see cref="Plan"/>'s remarks), existing Organizations keep
/// pointing at the old, now-inactive row - a price increase never silently changes what a current
/// subscriber is paying, and a historical row keeps reporting the price that was actually charged at
/// the time rather than today's price under the same name.
/// </para>
/// </remarks>
[Table("Subscription")]
public class Subscription : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;

    /// <summary>
    /// When this tenant went onto this <see cref="Plan"/>. Defaults to the row's creation instant -
    /// the database column carries <c>DEFAULT now()</c>, so a row inserted without one explicitly set
    /// is stamped by Postgres rather than left at <see cref="DateTimeOffset.MinValue"/>.
    /// </summary>
    public DateTimeOffset StartDate { get; set; }

    /// <summary>
    /// When this tenant came <em>off</em> this <see cref="Plan"/>, or <c>null</c> while it is still
    /// the current one. Null is the meaningful state here, not merely "unset": it is what the partial
    /// unique index keys on, so at most one row per tenant can carry it (see this class's remarks).
    /// </summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Where this subscription stands - and eventually whether its servers keep running. See
    /// <see cref="SubscriptionStatus"/>; nothing moves it off <see cref="SubscriptionStatus.Active"/>
    /// yet.
    /// </summary>
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    /// <summary>When <see cref="Status"/> last moved, or null if it has never moved off Active.</summary>
    public DateTimeOffset? StatusChangedOn { get; set; }

    /// <summary>
    /// Why <see cref="Status"/> was last changed, in the words of whoever changed it.
    /// </summary>
    /// <remarks>
    /// Suspension is the one action here that visibly breaks a paying customer's servers, and the first
    /// thing anyone asks afterwards is why it happened. A status with no reason beside it makes that
    /// unanswerable, which is the same argument <see cref="CreditNote.Reason"/> is required for - except
    /// this one is nullable, because a subscription that has never left Active has no reason to give.
    /// </remarks>
    [MaxLength(500)]
    public string? StatusReason { get; set; }

    /// <summary>The billing periods charged under this subscription - see
    /// <see cref="SubscriptionPeriod"/>.</summary>
    public ICollection<SubscriptionPeriod> Periods { get; set; } = [];
}
