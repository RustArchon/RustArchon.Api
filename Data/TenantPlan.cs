// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// Which <see cref="Plan"/> row a Tenant ("Organization") is currently assigned to - exactly one per
/// tenant, enforced by a unique index on <see cref="TenantId"/>.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated join table rather than a <c>PlanId</c> column added directly onto JumpStart's own
/// <see cref="Tenant"/> class - RustArchon doesn't fork or subclass JumpStart's framework entities
/// (see <c>ApiDbContext.OnModelCreating</c>'s remarks on <see cref="Tenant.Settings"/> for the same
/// reasoning applied elsewhere), and every other RustArchon-owned entity that relates to a tenant
/// already follows the "reference <c>TenantId</c>, don't touch <c>Tenant</c> itself" pattern (see
/// <see cref="RustServer"/>). This is the first *required, exactly-one-per-tenant* relationship in
/// this codebase rather than the usual one-to-many, which is why it's its own table instead of just
/// another <c>ITenantScoped</c> child row.
/// </para>
/// <para>
/// Every Organization must have exactly one of these from the moment it's created
/// (<c>AccountBootstrapController.EnsureTenant</c> assigns the cheapest currently-active Plan
/// immediately after creating the Tenant) - there is no supported "no plan" state. Deliberately points
/// at a specific <see cref="Plan.Id"/>, not just a <see cref="Plan.Name"/>: when a subscribed-to Plan
/// is superseded (see <see cref="Plan"/>'s remarks), existing Organizations keep pointing at the old,
/// now-inactive row - a price increase never silently changes what a current subscriber is paying.
/// </para>
/// </remarks>
[Table("TenantPlan")]
[Index(nameof(TenantId), IsUnique = true, Name = "IX_TenantPlan_TenantId")]
public class TenantPlan : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;

    public DateTimeOffset AssignedAtUtc { get; set; }
}
