// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// Money arriving from a tenant, independent of what it settles.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not attached to an invoice. One payment can settle several invoices and one invoice can
/// take several payments, so what a payment covers lives in <see cref="PaymentAllocation"/> rather than
/// in a foreign key here. A <c>PaidOn</c> column on an invoice - which is what this replaces - can
/// express neither.
/// </para>
/// <para>
/// The failure fields exist so a decline is diagnosable. "Payment failed" with nothing else recorded is
/// the difference between a support conversation that takes a minute and one that takes a day.
/// </para>
/// </remarks>
[Table("Payment")]
public class Payment : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>The gross amount received. Always positive - a refund reverses allocations and moves
    /// <see cref="Status"/>, rather than being recorded as a negative payment.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "char(3)")]
    public string Currency { get; set; } = "USD";

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public PaymentMethod Method { get; set; } = PaymentMethod.Manual;

    /// <summary>When the money arrived - which is not necessarily when this row was written.</summary>
    public DateTimeOffset ReceivedOn { get; set; }

    /// <summary>The provider's machine-readable decline reason, when there is one.</summary>
    [MaxLength(100)]
    public string? FailureCode { get; set; }

    /// <summary>The provider's human-readable decline reason, when there is one.</summary>
    [MaxLength(500)]
    public string? FailureMessage { get; set; }

    /// <summary>The payment provider's own id, once one exists.</summary>
    [MaxLength(255)]
    public string? ProviderPaymentId { get; set; }

    /// <summary>Optional note for a manually-recorded payment - a cheque number, a bank reference.</summary>
    [MaxLength(500)]
    public string? Reference { get; set; }

    public ICollection<PaymentAllocation> Allocations { get; set; } = [];
}

/// <summary>
/// How much of one <see cref="Payment"/> settles one <see cref="Invoice"/>.
/// </summary>
/// <remarks>
/// The join that makes partial payment, overpayment and multi-invoice settlement all expressible with
/// the same shape. A payment with no allocations is money received and not yet applied - which is a real
/// state, not an error, and one a receivables report should be able to see.
/// </remarks>
[Table("PaymentAllocation")]
public class PaymentAllocation : Entity
{
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;

    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    /// <summary>How much of the payment this applies to that invoice.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal Amount { get; set; }

    public DateTimeOffset AllocatedOn { get; set; }

    /// <summary>
    /// Set when this allocation is undone by a refund or a chargeback, rather than the row being deleted.
    /// </summary>
    /// <remarks>
    /// Reversal is a fact worth keeping. Deleting the row would make an invoice reopen with no record of
    /// why, and "this was paid and then charged back" is precisely what someone looking at an
    /// unexpectedly-open invoice needs to be told.
    /// </remarks>
    public DateTimeOffset? ReversedOn { get; set; }
}

/// <summary>
/// Value granted back to a tenant without money moving - goodwill, an outage, or a correction to an
/// invoice that has already been finalised.
/// </summary>
/// <remarks>
/// Needed even under a no-refunds policy, and for exactly that reason: a finalised invoice is immutable,
/// so the only way to reduce what someone owes is to issue something that offsets it. Without this the
/// options are editing history or refusing to fix a mistake.
/// </remarks>
[Table("CreditNote")]
public class CreditNote : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>The value granted. Positive - it reduces what is owed.</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "char(3)")]
    public string Currency { get; set; } = "USD";

    /// <summary>Why this was issued. Required - an unexplained credit is indistinguishable from an error.</summary>
    [Required]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset IssuedOn { get; set; }

    /// <summary>
    /// The invoice this reduces, or <c>null</c> for a credit held against the tenant's account to be
    /// applied to something later.
    /// </summary>
    public Guid? AppliedToInvoiceId { get; set; }
    public Invoice? AppliedToInvoice { get; set; }
}
