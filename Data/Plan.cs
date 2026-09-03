// Copyright ©2026 Scott Blomfield

using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data.Auditing;

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
/// (<see cref="TenantPlan"/>) is never edited in place - a price change, for instance, creates a new
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

    [Column(TypeName = "numeric(18,2)")]
    public decimal MonthlyPrice { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal QuarterlyPrice { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal AnnualPrice { get; set; }

    /// <summary>How many days of console/chat/player history this plan retains.</summary>
    public int RetentionHistory { get; set; }

    /// <summary>Whether this plan allows role separation (e.g. Owner vs. Admin) within an Organization.</summary>
    public bool HasRoles { get; set; }

    public int MaximumServers { get; set; }
    public int MaximumUsers { get; set; }

    /// <summary>
    /// Whether this is the current, live version of its <see cref="Name"/> - see this class's own
    /// remarks for the full "at most one active per Name" rule and how it's enforced.
    /// </summary>
    public bool Active { get; set; }
}
