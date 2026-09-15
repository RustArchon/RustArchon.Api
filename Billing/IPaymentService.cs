// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <summary>
/// Settling invoices: recording money that arrived, granting value back, and closing debts that will
/// never be paid.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An issued invoice is immutable.</strong> Nothing here edits one - every operation writes a
/// new row alongside it (a payment and its allocation, a credit note) and lets the invoice's balance
/// fall out of the arithmetic. That is what makes the history readable afterwards: an invoice that
/// reopened because a payment was charged back still has the payment, the allocation, and the reversal
/// on it, rather than a status that quietly moved back.
/// </para>
/// <para>
/// <strong>Manual entry is the first implementation on purpose.</strong> It exercises allocation,
/// partial payment, over-payment and write-off with no provider to integrate against, so the model is
/// proven before a Stripe webhook is pointed at it - at which point the adapter's job is to call these
/// same methods rather than to invent a second path to the same tables.
/// </para>
/// </remarks>
public interface IPaymentService
{
    /// <summary>
    /// Records money received and applies it, starting with the named invoice.
    /// </summary>
    /// <remarks>
    /// Over-payment is allowed and is not an error: the excess spills onto the Organization's other open
    /// invoices oldest-due first, and anything still left stays unallocated against the account. Refusing
    /// it would mean rejecting a real bank transfer because it did not match a figure exactly, which is
    /// the sort of rule that gets worked around with spreadsheets.
    /// </remarks>
    /// <param name="providerPaymentId">
    /// A payment gateway's own id for this payment (e.g. a Stripe PaymentIntent id), when one exists -
    /// null for a manually-entered payment. When set, this call is idempotent against redelivery: a
    /// second call with the same <paramref name="providerPaymentId"/> returns the payment already
    /// recorded rather than recording a duplicate - see <c>PaymentService.RecordPaymentAsync</c>'s
    /// remarks. A webhook adapter's whole job is passing this through; nothing else in this codebase
    /// has one to give.
    /// </param>
    /// <returns>The payment, with its allocations.</returns>
    Task<Payment> RecordPaymentAsync(
        Guid invoiceId, decimal amount, PaymentMethod method, DateTimeOffset? receivedOn,
        string? reference, string? providerPaymentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes some or all of a payment - a refund or a chargeback - by reversing its allocations rather
    /// than editing anything it settled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every invoice it touched recalculates and reopens if it is owed again. The allocation rows stay;
    /// what changes is <see cref="PaymentAllocation.ReversedAmount"/>, because "this was paid and then
    /// (fully or partly) charged back" is exactly what someone looking at an unexpectedly-open invoice
    /// needs to be told.
    /// </para>
    /// <para>
    /// For a payment collected through Stripe (<see cref="Payment.ProviderPaymentId"/> set), this also
    /// calls Stripe's own Refund API - and, for any invoice with a
    /// <see cref="Invoice.TaxTransactionId"/>, reverses the matching share of tax too - before touching
    /// this codebase's own books, never after. See <c>PaymentService</c>'s own remarks for why a Stripe
    /// failure here must leave the books exactly as they were, not partially updated.
    /// </para>
    /// </remarks>
    /// <param name="amount">
    /// How much to reverse, or <c>null</c> to reverse everything still live on this payment (its full
    /// remaining, un-reversed total - not necessarily its original <see cref="Payment.Amount"/>, if an
    /// earlier partial reversal already happened). Multiple partial reversals against the same payment
    /// are allowed, applied oldest-allocation-first, the same ordering
    /// <see cref="RecordPaymentAsync"/>'s own overpayment spillover already uses.
    /// </param>
    /// <returns><c>false</c> when the payment doesn't exist, isn't currently <see cref="PaymentStatus.Succeeded"/>,
    /// or <paramref name="amount"/> exceeds what's still live to reverse.</returns>
    Task<bool> ReversePaymentAsync(
        Guid paymentId, PaymentStatus status, decimal? amount = null, CancellationToken cancellationToken = default);

    /// <summary>Grants value back against an invoice without money moving.</summary>
    Task<CreditNote?> IssueCreditNoteAsync(
        Guid invoiceId, decimal amount, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an invoice as never having been owed. Removes it from receivables entirely.
    /// </summary>
    /// <remarks>
    /// For an invoice raised in error. Not the same as <see cref="WriteOffAsync"/>, and the difference
    /// is the one an auditor cares about - see <see cref="InvoiceStatus.Void"/>. The number is kept and
    /// stays in the sequence; a gap is what a gapless requirement exists to prevent. Refused once
    /// anything has been paid against it, because voiding a settled invoice would strand the payment
    /// with nothing to explain it - reverse the payment first.
    /// </remarks>
    Task<bool> VoidInvoiceAsync(Guid invoiceId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives up on a debt that was genuinely owed, keeping it in the books as bad debt.
    /// </summary>
    Task<bool> WriteOffInvoiceAsync(Guid invoiceId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Invoices for the admin screen, newest first, optionally narrowed to one status.</summary>
    Task<IReadOnlyList<InvoiceDto>> GetInvoicesAsync(
        InvoiceStatus? status = null, Guid? tenantId = null, CancellationToken cancellationToken = default);
}
