// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;

namespace RustArchon.Api.Data;

/// <summary>Where one <see cref="DiscountRedemption"/> stands.</summary>
public enum DiscountRedemptionStatus
{
    /// <summary>Reserved against a code's limits, but not yet reduced any invoice - it will be, the
    /// next time one is issued for this tenant. See <c>InvoiceService.IssueForPeriodAsync</c>.</summary>
    Pending,

    /// <summary>Spent - reduced exactly one invoice, recorded on <see cref="InvoiceId"/>/
    /// <see cref="DiscountAmount"/>.</summary>
    Applied
}

/// <summary>
/// One Organization's claim on one <see cref="Discount"/> - the fact of redemption, distinct from the
/// code's own rules. Never deleted: a spent redemption is the permanent record of what an invoice was
/// discounted for and why.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not tenant-scoped through JumpStart's global filter</strong> - the same reasoning as
/// <see cref="Invoice"/>/<see cref="Payment"/>/<see cref="BlockedInvoiceIssuance"/> (see their own
/// remarks): the background invoicing pass that consumes a <see cref="DiscountRedemptionStatus.Pending"/>
/// row has no ambient tenant, and the abuse-signal report reads across every tenant on purpose.
/// </para>
/// <para>
/// <strong>At most one <see cref="DiscountRedemptionStatus.Pending"/> row per tenant at any time.</strong>
/// Enforced by <see cref="Billing.DiscountService.RedeemAsync"/>, not by a database constraint - this is
/// what "one coupon per order/renewal" actually means: a second redemption attempt while one is still
/// unconsumed is refused outright, rather than queued behind the first.
/// </para>
/// </remarks>
[Table("DiscountRedemption")]
public class DiscountRedemption : Entity
{
    public Guid DiscountId { get; set; }
    public Discount Discount { get; set; } = null!;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public DiscountRedemptionStatus Status { get; set; } = DiscountRedemptionStatus.Pending;

    public DateTimeOffset RedeemedOn { get; set; }

    /// <summary>The invoice this reduced - set only once <see cref="Status"/> reaches
    /// <see cref="DiscountRedemptionStatus.Applied"/>.</summary>
    public Guid? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    /// <summary>The actual currency amount taken off - set only once applied. Recorded even for a
    /// percentage discount, so the redemption's own history reads in dollars without needing to
    /// recompute it against whatever the invoice happened to total.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal? DiscountAmount { get; set; }

    public DateTimeOffset? AppliedOn { get; set; }
}
