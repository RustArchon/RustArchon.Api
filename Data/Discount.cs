// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// A discount code: the rules an admin sets (how much off, how it may be used, when it stops being
/// valid) - not the fact that any particular Organization has used it, which is
/// <see cref="DiscountRedemption"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One code per discount, deliberately.</strong> Stripe's own model splits a Coupon (the rules)
/// from one or more Promotion Codes (the redeemable strings) so one set of rules can have many distinct
/// codes - e.g. a different code per affiliate, all worth the same discount. RustArchon doesn't need that
/// yet, so <see cref="Code"/> lives directly on this row rather than a separate table. If a multi-code
/// need shows up later, splitting <see cref="Code"/> out into its own table (keyed to this one) is the
/// natural extension - nothing about <see cref="DiscountRedemption"/> would need to change, since it
/// already references the rules by <see cref="DiscountRedemption.DiscountId"/> rather than by the code
/// string itself.
/// </para>
/// <para>
/// <strong>Immutable once created, except <see cref="IsActive"/>.</strong> There is deliberately no
/// "edit" endpoint - a code someone has already redeemed under one set of rules should never quietly
/// start meaning something else. Made a mistake creating one? Deactivate it and create the one you meant.
/// </para>
/// <para>
/// <strong>No stacking.</strong> <see cref="Billing.DiscountService.RedeemAsync"/> refuses a new
/// redemption while the same tenant already has an unconsumed (<see cref="DiscountRedemptionStatus.Pending"/>)
/// one from any discount - "one coupon per order/renewal" is enforced there, not here.
/// </para>
/// </remarks>
[Table("Discount")]
[Index(nameof(Code), IsUnique = true, Name = "IX_Discount_Code")]
public class Discount : Entity
{
    /// <summary>The string a customer types in, or an admin quotes when assigning one - always stored
    /// and compared in upper case (see <see cref="Billing.DiscountService"/>).</summary>
    [Required]
    [MaxLength(40)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Admin-facing label - "why this code exists," never shown to a customer.</summary>
    [MaxLength(200)]
    public string? Description { get; set; }

    public DiscountAmountType AmountType { get; set; }

    /// <summary>A percentage (0-100) or a flat currency amount, depending on <see cref="AmountType"/>.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal AmountValue { get; set; }

    public DiscountFrequency Frequency { get; set; } = DiscountFrequency.OneTime;

    /// <summary>How many times this code may be redeemed in total, across every Organization - null
    /// means open/unlimited ("general consumption").</summary>
    public int? MaxRedemptions { get; set; }

    /// <summary>
    /// Running count of redemptions, incremented the moment one is created (not once it's applied to an
    /// invoice) - see <see cref="Billing.DiscountService.RedeemAsync"/>'s own remarks for why counting at
    /// reservation time, not at spend time, is what actually prevents overselling a capped code.
    /// </summary>
    public int TimesRedeemed { get; set; }

    /// <summary>When true, one Organization may redeem this code at most once - see
    /// <see cref="Billing.DiscountService"/>'s own remarks on why this check is necessarily best-effort
    /// (a new Organization is trivial to create).</summary>
    public bool OncePerOrganization { get; set; }

    /// <summary>
    /// Null for "general consumption" - anyone may redeem it. Set to restrict this code to one specific
    /// Organization only, e.g. an admin-negotiated rate for a single customer.
    /// </summary>
    public Guid? RestrictedToTenantId { get; set; }
    public Tenant? RestrictedToTenant { get; set; }

    /// <summary>Null means it never expires on its own (still stoppable via <see cref="IsActive"/>).</summary>
    public DateTimeOffset? ExpiresOn { get; set; }

    /// <summary>An admin's manual off-switch - a code that has run its course without needing to hit any
    /// of the other limits, or one created by mistake.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this code was created - a plain, self-managed timestamp rather than
    /// <c>JumpStart.Data.Auditing.AuditableEntity</c>'s <c>CreatedOn</c>, since that one is populated by
    /// the repository layer's <c>AddAsync</c> and <see cref="Billing.DiscountService"/> writes directly
    /// through <c>ApiDbContext</c> instead, the same as <see cref="TenantBillingAddress"/> and
    /// <see cref="BlockedInvoiceIssuance"/>.
    /// </summary>
    public DateTimeOffset CreatedOn { get; set; }

    public ICollection<DiscountRedemption> Redemptions { get; set; } = [];
}
