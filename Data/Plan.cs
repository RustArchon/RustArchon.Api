// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data.Auditing;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// One historical version of a pricing tier - not tenant-scoped (this is platform-wide catalog data,
/// managed by a site admin, not any one Organization's own data).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not a fixed enum.</strong> This catalog (and the marketing site that presents it) isn't
/// part of RustArchon's AGPL-licensed product - a self-hoster running their own Panel/Api can run
/// their own marketing site with their own tiers, and shouldn't be restricted to RustArchon's own
/// "Wood/Stone/Metal/HQM" naming baked into open-source code. <see cref="Name"/> and
/// <see cref="ColorCode"/> are free-form admin input instead, playing the identity role
/// <c>PlanType</c> used to.
/// </para>
/// <para>
/// <strong>Many rows can share a <see cref="Name"/> over time.</strong> Per policy (see the Panel
/// admin page's remarks), a Plan already assigned to one or more Organizations
/// (<see cref="Subscription"/>) is never edited in place - a price change, for instance, creates a new
/// row and deactivates the old one instead, so existing Organizations keep whatever terms they signed
/// up under. A Plan nobody has subscribed to yet can still be edited directly. This is why
/// <see cref="Active"/> exists at all: it, not <see cref="Name"/> alone, is what a brand-new
/// Organization actually gets assigned (the cheapest currently-active Plan - see
/// <c>AccountBootstrapController</c>) and what RustArchon.Web's pricing page displays.
/// </para>
/// <para>
/// <strong>At most one row per <see cref="Name"/> may have <see cref="Active"/> = <c>true</c></strong>
/// at any moment - enforced both by a partial unique index on <c>(Name) WHERE "Active"</c>
/// (Fluent API in <c>ApiDbContext.OnModelCreating</c> - a plain <c>[Index]</c> data annotation can't
/// express the filter, and without one a unique index on <see cref="Name"/> alone would wrongly limit
/// this table to one row per Name ever, defeating the whole point of keeping historical rows) and,
/// before that index would ever be hit, by every write path that sets <c>Active: true</c>
/// (<c>PlansController</c>'s Create/Update/Supersede actions all deactivate any other currently-active
/// Plan with the same Name first, via <c>IPlanRepository.DeactivateOtherActiveAsync</c>).
/// </para>
/// </remarks>
[Table("Plan")]
public class Plan : AuditableEntity
{
    /// <summary>Admin-chosen display name (e.g. "Wood", "Starter", "Pro") - see this class's remarks.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex color (e.g. <c>#b08553</c>) used for this plan's swatch/accent on the marketing site.</summary>
    [Column(TypeName = "varchar(7)")]
    public string ColorCode { get; set; } = "#888888";

    /// <summary>
    /// What this plan costs, one row per term it is offered on - see <see cref="PlanPrice"/>. A plan with
    /// a single Annual row is sold annually and not otherwise.
    /// </summary>
    public ICollection<PlanPrice> Prices { get; set; } = [];

    /// <summary>
    /// Whether capacity is capped at a fixed ceiling or bought by the unit. Drives how the plan is
    /// presented and validated, not how its price is calculated - see <see cref="PlanPrice"/>.
    /// </summary>
    public PricingModel PricingModel { get; set; } = PricingModel.Flat;

    /// <summary>How many days of console/chat/player history this plan retains.</summary>
    public int RetentionHistory { get; set; }

    /// <summary>Whether this plan allows role separation (e.g. Owner vs. Admin) within an Organization.</summary>
    public bool HasRoles { get; set; }

    /// <summary>
    /// Whether one person may only ever have a single Organization of their own on this plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set on the free tier, where an unlimited supply of Organizations would be an unlimited supply
    /// of free server slots. A flag rather than an inference from price, because "costs nothing" and
    /// "should be rationed" are not the same question - a paid trial or a promotional tier would want
    /// this too, and a free plan that is genuinely meant to be unlimited should be able to say so.
    /// It also puts the decision where a site admin can change it, instead of in a constant.
    /// </para>
    /// <para>
    /// Counted against whoever <em>created</em> the Organization, not whoever owns it now - being
    /// handed the Owner role in somebody else's Organization is ordinary and should not spend your
    /// own allowance. Cancelled Organizations do not count.
    /// </para>
    /// <para>
    /// Worth being clear that this is a speed bump, not a control: a second email address defeats it.
    /// It exists to stop the casual and the accidental, and it costs a real customer nothing, because
    /// the Organization they are adding is one they are paying for.
    /// </para>
    /// </remarks>
    public bool OnePerOwner { get; set; }

    /// <summary>
    /// The most servers an Organization on this plan may hold, or <c>null</c> for no ceiling.
    /// </summary>
    /// <remarks>
    /// Nullable because a per-unit plan doesn't cap capacity, it charges for it - there is no number of
    /// servers you are forbidden to have, only a number you have not paid for. Every guard that used to
    /// read this unconditionally now has to treat null as "no ceiling to enforce" and fall through to the
    /// entitlement check instead.
    /// </remarks>
    public int? MaximumServers { get; set; }

    public int MaximumUsers { get; set; }

    /// <summary>
    /// Whether this is the current, live version of its <see cref="Name"/> - see this class's own
    /// remarks for the full "at most one active per Name" rule and how it's enforced.
    /// </summary>
    public bool Active { get; set; }
}
