// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;

namespace RustArchon.Api.Data;

/// <summary>
/// One charge on an <see cref="Invoice"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Amount"/> may be negative.</strong> That is how a proration credit is expressed -
/// a mid-period change that reduces what is owed for the remainder of a period produces a negative line
/// on the same invoice as the positive one that replaces it, so the arithmetic is visible on the
/// document rather than hidden in a net figure. The same shape carries a discount or an adjustment when
/// those arrive.
/// </para>
/// <para>
/// <see cref="SubscriptionPeriodId"/> is the single link between the subscription band and the billing
/// band - where revenue earned becomes money asked for. It is nullable because not every charge comes
/// from a period: a one-off fee, a manual adjustment and a discount all belong on an invoice without
/// belonging to a span of service.
/// </para>
/// </remarks>
[Table("InvoiceLine")]
public class InvoiceLine : Entity
{
    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    /// <summary>What this line is for, as the tenant reads it on the document.</summary>
    [Required]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Start of the span of service this charge covers.</summary>
    public DateTimeOffset ServiceStart { get; set; }

    /// <summary>End of the span of service this charge covers.</summary>
    public DateTimeOffset ServiceEnd { get; set; }

    /// <summary>How many units this line charges for - server slots, usually.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Price per unit. <see cref="Amount"/> is not required to equal quantity times this: a
    /// prorated line charges a fraction of it.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal UnitAmount { get; set; }

    /// <summary>What this line adds to the invoice, net of tax. May be negative - see this class's
    /// remarks.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>Tax on this line, stored apart from <see cref="Amount"/> rather than folded into it.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal TaxAmount { get; set; }

    /// <summary>
    /// The billing period this charge bills for, when it bills for one. See this class's remarks.
    /// </summary>
    public Guid? SubscriptionPeriodId { get; set; }
    public SubscriptionPeriod? SubscriptionPeriod { get; set; }
}
