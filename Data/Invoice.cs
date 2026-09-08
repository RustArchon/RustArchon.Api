// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// A document asking a tenant for money.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable once finalised.</strong> A finalised invoice is a statement of what was asked for
/// on a date, and corrections happen by issuing a <see cref="CreditNote"/> against it - never by editing
/// it. That is why <see cref="AmountPaid"/> and <see cref="AmountCredited"/> are maintained totals
/// rather than the invoice being rewritten as money arrives, and why <see cref="Status"/> carries no
/// "partially paid" value: partial payment is <see cref="Open"/> with some of the total allocated, and
/// is derived rather than stored.
/// </para>
/// <para>
/// Separate from <see cref="SubscriptionPeriod"/> on purpose. A period records revenue <em>earned</em>;
/// this records money <em>asked for</em>. They are different dates and different amounts - which is what
/// lets a tenant be billed annually while revenue is recognised monthly - and they meet only where an
/// <see cref="InvoiceLine"/> points back at a period.
/// </para>
/// <para>
/// <strong>Nothing creates one yet.</strong> The table exists so the shape is settled before invoice
/// generation is written against it.
/// </para>
/// </remarks>
[Table("Invoice")]
public class Invoice : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// The human-facing invoice number, or <c>null</c> while this is still a draft.
    /// </summary>
    /// <remarks>
    /// Allocated at finalisation rather than at creation, which is the whole reason the sequence stays
    /// gapless: a draft that is abandoned must not consume a number. See
    /// <see cref="InvoiceNumberSequence"/> for how one is taken. A voided invoice keeps its number and
    /// stays in the sequence - a missing number is exactly what a gapless requirement exists to prevent.
    /// </remarks>
    [MaxLength(40)]
    public string? Number { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    /// <summary>ISO 4217 code for every amount on this invoice and its lines.</summary>
    [Column(TypeName = "char(3)")]
    public string Currency { get; set; } = "USD";

    /// <summary>When this was finalised and became owed. Null while <see cref="InvoiceStatus.Draft"/>.</summary>
    public DateTimeOffset? IssuedOn { get; set; }

    /// <summary>
    /// When payment is due. Null while draft. An invoice is past due when it is
    /// <see cref="InvoiceStatus.Open"/> and this has passed - a derived state, never stored.
    /// </summary>
    public DateTimeOffset? DueOn { get; set; }

    /// <summary>Sum of the lines' net amounts, before tax.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal Subtotal { get; set; }

    /// <summary>
    /// Sum of the lines' tax. Stored separately from net throughout - the fields are modelled now and
    /// the calculation is left to a provider later, because adding the columns costs nothing today and
    /// discovering that historical amounts silently included tax costs a great deal.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal TaxTotal { get; set; }

    /// <summary><see cref="Subtotal"/> plus <see cref="TaxTotal"/> - what is owed.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal Total { get; set; }

    /// <summary>
    /// How much of <see cref="Total"/> has been settled by payments. Maintained as
    /// <see cref="PaymentAllocation"/> rows are written and reversed.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal AmountPaid { get; set; }

    /// <summary>How much of <see cref="Total"/> has been cancelled by credit notes.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal AmountCredited { get; set; }

    /// <summary>Set when the invoice is voided - see <see cref="InvoiceStatus.Void"/>.</summary>
    public DateTimeOffset? VoidedOn { get; set; }

    /// <summary>Set when the debt is given up on - see <see cref="InvoiceStatus.Uncollectible"/>.</summary>
    public DateTimeOffset? WrittenOffOn { get; set; }

    /// <summary>The payment provider's own id for this invoice, once one exists.</summary>
    [MaxLength(255)]
    public string? ProviderInvoiceId { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = [];

    /// <summary>
    /// What is still owed: the total, less what has been paid and credited. Never negative.
    /// </summary>
    /// <remarks>
    /// Computed rather than stored, and the reason receivables reports can be trusted: a stored balance
    /// is a fourth number that has to be kept in step with three others, and the first time it drifts
    /// nothing says which one is wrong.
    /// </remarks>
    [NotMapped]
    public decimal AmountOutstanding => Math.Max(0m, Total - AmountPaid - AmountCredited);
}
