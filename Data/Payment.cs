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

    /// <summary>
    /// The payment provider's own id for the specific event that produced this row (a Stripe
    /// <c>evt_...</c> id), set only for a <see cref="PaymentStatus.Failed"/> row recorded from a webhook.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ProviderPaymentId"/> on purpose: a Stripe PaymentIntent can fail more
    /// than once (a customer retries with a different card on the same Checkout Session), so deduping a
    /// failure by <see cref="ProviderPaymentId"/> the way <c>PaymentService.RecordPaymentAsync</c> dedupes
    /// a success would silently drop every failure after the first genuinely distinct decline on the same
    /// PaymentIntent. A webhook's own event id is unique per occurrence, including redelivery of the
    /// identical event, which is exactly the idempotency key a failure needs.
    /// </remarks>
    [MaxLength(255)]
    public string? ProviderEventId { get; set; }

    /// <summary>Stripe's own id for the chargeback against this payment, set the moment a
    /// <c>charge.dispute.created</c> webhook is recorded - see <c>PaymentService.RecordDisputeAsync</c>.</summary>
    [MaxLength(255)]
    public string? DisputeId { get; set; }

    /// <summary>Stripe's own reason code for the dispute (e.g. <c>fraudulent</c>, <c>product_not_received</c>).</summary>
    [MaxLength(100)]
    public string? DisputeReason { get; set; }

    /// <summary>Stripe's own deadline for submitting evidence - the countdown the chargeback packet
    /// page shows.</summary>
    public DateTimeOffset? DisputeDueBy { get; set; }

    /// <summary>
    /// When a site admin submitted evidence to Stripe for this dispute - null until they do. Evidence
    /// can technically be updated again before <see cref="DisputeDueBy"/>, but this is deliberately not
    /// cleared on a later submission (it always reflects the <em>first</em> submission) - the chargeback
    /// packet page's "already submitted" warning exists specifically to make a second submission a
    /// deliberate choice, not something that looks unsent again.
    /// </summary>
    public DateTimeOffset? DisputeEvidenceSubmittedOn { get; set; }

    /// <summary>
    /// Stripe's own final outcome for the dispute - <c>won</c>, <c>lost</c>, or <c>warning_closed</c> -
    /// set the moment a <c>charge.dispute.closed</c> webhook is recorded. Null until the dispute closes.
    /// A <c>lost</c> dispute needs nothing further done to it: the money is already gone (see
    /// <see cref="DisputeId"/>'s own remarks - a chargeback never had a Stripe refund call to reverse in
    /// the first place), so this is purely the record that the case is over rather than still pending. A
    /// <c>won</c> dispute doesn't reinstate anything by itself either - see
    /// <see cref="DisputeFundsReinstatedOn"/> for why Stripe reports the outcome decision and the money
    /// actually moving back as two separate events.
    /// </summary>
    [MaxLength(50)]
    public string? DisputeStatus { get; set; }

    /// <summary>When <c>charge.dispute.closed</c> was recorded for this payment's dispute.</summary>
    public DateTimeOffset? DisputeClosedOn { get; set; }

    /// <summary>
    /// How much <c>PaymentService.ApplyReversalAsync</c> actually reversed when this dispute was first
    /// recorded (see <c>PaymentService.RecordDisputeAsync</c>) - captured here so a later
    /// <c>charge.dispute.funds_reinstated</c> knows exactly how much to give back without re-deriving it
    /// from allocations that may have changed since (a partial refund issued some other way while the
    /// dispute was still open, for instance). Zero if there was nothing live left to reverse at the time.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal DisputeAmountReversed { get; set; }

    /// <summary>
    /// When <c>charge.dispute.funds_reinstated</c> was recorded - Stripe's own signal that a won
    /// dispute's money actually came back into the Stripe balance, not just that the outcome was
    /// decided. Deliberately a separate moment from <see cref="DisputeClosedOn"/>: Stripe can report a
    /// dispute <c>won</c> before the funds move, and <c>PaymentService.RecordDisputeFundsReinstatedAsync</c>
    /// is what actually re-settles the invoice - a decided-but-not-yet-reinstated win leaves the invoice
    /// reopened on purpose, since the money genuinely isn't back yet.
    /// </summary>
    public DateTimeOffset? DisputeFundsReinstatedOn { get; set; }

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
    /// Set once this allocation has been reversed <em>in full</em> - null while it hasn't been touched
    /// at all, and still null while only part of it has (see <see cref="ReversedAmount"/>). Deleting the
    /// row instead would make an invoice reopen with no record of why, and "this was paid and then
    /// charged back" is precisely what someone looking at an unexpectedly-open invoice needs to be told.
    /// </summary>
    public DateTimeOffset? ReversedOn { get; set; }

    /// <summary>
    /// How much of <see cref="Amount"/> has been given back so far, via one or more partial refunds -
    /// zero if none has. An allocation is still "live" (available to reverse further) whenever this is
    /// less than <see cref="Amount"/>, regardless of whether <see cref="ReversedOn"/> is set; the two
    /// only agree once this reaches <see cref="Amount"/> exactly, which is what actually sets
    /// <see cref="ReversedOn"/>.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal ReversedAmount { get; set; }
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
