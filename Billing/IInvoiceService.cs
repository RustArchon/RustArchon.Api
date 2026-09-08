// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Api.Data;

namespace RustArchon.Api.Billing;

/// <summary>
/// Turns what a subscription earned into a document asking for money.
/// </summary>
/// <remarks>
/// <para>
/// The single crossing between the subscription band and the billing band. Everything upstream of this
/// records revenue <em>earned</em> - periods, slices, prorated amounts - and everything downstream
/// records money <em>asked for</em> and what came back. Keeping the crossing in one place is what stops
/// the two from being conflated again.
/// </para>
/// <para>
/// <strong>Two occasions produce an invoice</strong>, and they are the two the billing design settled
/// on. A billing period starting - a sign-up or a renewal - is invoiced for the period's price. A change
/// landing mid-period is invoiced immediately for the prorated difference, on its own document rather
/// than as a line on the next period's invoice: the alternative grants the upgrade before anything is
/// charged, so a tenant could upgrade, use it, and leave without ever being billed.
/// </para>
/// <para>
/// <strong>An issued invoice is never revised.</strong> A later mid-period change re-prices the
/// <em>earned</em> amount of the slice it closes, which is revenue recognition and has nothing to say
/// about a document already sent. Corrections to a finalised invoice happen by credit note.
/// </para>
/// </remarks>
public interface IInvoiceService
{
    /// <summary>
    /// Issues a finalised, one-line invoice for <paramref name="period"/>, or returns <c>null</c> when
    /// there is nothing to bill.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>null</c> - rather than throwing or issuing an empty document - in the two cases that
    /// are normal rather than exceptional: an amount of zero or less (a free plan, or a change that
    /// costs nothing now), and a period that has already been billed. The second is what makes this
    /// safe to call from the background scheduler, which can legitimately run over the same period twice
    /// after a restart.
    /// </para>
    /// <para>
    /// Enlists in whatever transaction the caller has open. That matters: the invoice number is taken
    /// under a row lock held until commit, so a caller that rolls back releases the number rather than
    /// burning it.
    /// </para>
    /// </remarks>
    /// <param name="period">The billing period being charged for. Must already be saved.</param>
    /// <param name="amount">
    /// What to charge, net of tax. Not read from the period: a period-start invoice bills the period's
    /// full price, while a mid-period change bills only the prorated difference.
    /// </param>
    /// <param name="description">The line's description, as the tenant reads it on the document.</param>
    Task<Invoice?> IssueForPeriodAsync(
        SubscriptionPeriod period, decimal amount, string description, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether any invoice line already charges for this period - the guard against billing twice.
    /// </summary>
    /// <remarks>
    /// Lines on voided invoices don't count: a document issued in error was never owed, so the period
    /// behind it is unbilled again and must be re-issuable.
    /// </remarks>
    Task<bool> HasBeenBilledAsync(Guid periodId, CancellationToken cancellationToken = default);
}
