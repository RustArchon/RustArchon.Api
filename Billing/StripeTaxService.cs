// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RustArchon.Api.Data;
using Stripe;

namespace RustArchon.Api.Billing;

/// <summary>The result of a successful Stripe Tax calculation - see <see cref="IStripeTaxService"/>.</summary>
/// <param name="TaxAmount">What's owed in tax, in this codebase's decimal-dollars convention.</param>
/// <param name="TransactionId">
/// Stripe Tax's own transaction id - stored on <see cref="Invoice.TaxTransactionId"/> so a future
/// refund can reverse it alongside the payment.
/// </param>
public sealed record StripeTaxResult(decimal TaxAmount, string TransactionId);

/// <summary>
/// Calculates (and records, for Stripe's own remittance reporting) the sales tax owed on one invoice
/// line, using Stripe Tax.
/// </summary>
public interface IStripeTaxService
{
    /// <summary>
    /// Calculates tax on <paramref name="amount"/> for <paramref name="tenantId"/>, and immediately
    /// commits it as a Stripe Tax transaction - the pairing Stripe's own reporting/remittance depends
    /// on (see <see cref="StripeTaxService"/>'s remarks for why this isn't two separate steps a caller
    /// could do independently).
    /// </summary>
    /// <param name="referenceId">
    /// A caller-chosen identifier, unique across every calculation this tenant will ever make (an
    /// invoice's own id is the obvious choice) - Stripe requires this to key its own transaction
    /// records and any later reversal.
    /// </param>
    /// <returns>
    /// <c>null</c> if the tenant has no billing address on file at all (see
    /// <see cref="TenantBillingAddress"/>'s remarks - no country means no jurisdiction to calculate
    /// against, not an error). Otherwise the calculated tax and the transaction id to keep.
    /// </returns>
    Task<StripeTaxResult?> CalculateAsync(
        Guid tenantId, decimal amount, string currency, string referenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses part of a previously-committed tax transaction - the counterpart to
    /// <see cref="CalculateAsync"/> for a refund. Stripe determines the tax-vs-base split of
    /// <paramref name="amount"/> itself; the caller only ever names a flat, after-tax dollar figure
    /// (the same amount actually being refunded), never a separately-computed tax portion.
    /// </summary>
    /// <param name="taxTransactionId">The transaction to reverse - <see cref="Invoice.TaxTransactionId"/>.</param>
    /// <param name="amount">
    /// How much (in this codebase's decimal-dollars convention, always positive) of the original,
    /// tax-inclusive transaction to reverse.
    /// </param>
    /// <param name="referenceId">A caller-chosen identifier, unique across every reversal Stripe has
    /// ever seen from this account - distinct from the reference the original calculation/transaction
    /// used, since Stripe requires every reference to be unique.</param>
    Task ReverseAsync(
        string taxTransactionId, decimal amount, string referenceId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IStripeTaxService" />
/// <remarks>
/// <para>
/// <strong>One tax code for everything RustArchon sells.</strong> <see cref="TaxCode"/> -
/// <c>txcd_10103001</c>, "Software as a service (SaaS) - business use" - reflects that RustArchon
/// bills every customer as an Organization, not an individual; Stripe's own taxonomy only
/// distinguishes business/personal use for US sales, and this is a deliberate choice, not a guess (see
/// this class's own commit history/PR for the decision). If that ever needs to vary per plan or per
/// tenant, it stops being a constant and becomes a parameter - not a reason to guess a different code
/// here today.
/// </para>
/// <para>
/// <strong>Calculation, then transaction, in the same call.</strong> Stripe's Tax API splits these
/// into two steps (a `Calculation` you can preview without committing to anything, and a `Transaction`
/// that actually records the sale for remittance reporting) because some integrations want to show a
/// customer a tax-inclusive price before they commit to buying. RustArchon has no such preview step -
/// by the time this is called, the invoice is being finalised - so there is no reason to ever create a
/// Calculation without immediately turning it into a Transaction, and every caller of this interface
/// gets both for the price of one call.
/// </para>
/// <para>
/// <strong>Failure handling is the caller's decision, not this class's.</strong> A Stripe API failure
/// (network blip, an address Stripe can't resolve to a jurisdiction) is left to propagate as a
/// <see cref="StripeException"/> rather than swallowed here - see <c>InvoiceService.IssueForPeriodAsync</c>'s
/// own remarks for why it treats that the same as any other reason a period couldn't be billed this
/// pass (retried automatically next time), rather than issuing an invoice with silently-wrong ($0) tax
/// on it.
/// </para>
/// <para>
/// <strong>A calculated $0 isn't always an honest $0.</strong> Stripe never errors when RustArchon has
/// no active tax registration in a customer's jurisdiction - it silently returns zero tax for that line
/// instead (verified against Stripe's own docs: "Stripe only calculates tax in jurisdictions where you
/// have an active tax registration... [without one] the calculation returns zero tax", not an error).
/// Issuing an invoice on that silent zero would be wrong whenever the jurisdiction actually owes tax and
/// RustArchon simply hasn't registered there yet - as opposed to genuinely owing nothing (no sales tax in
/// that state, a B2B reverse charge, an exempt customer), which is a completely different, harmless
/// reason for the same $0. Stripe's own <c>taxability_reason</c> on each <c>tax_breakdown</c> line
/// distinguishes the two explicitly (<c>not_collecting</c> means the former), and since RustArchon never
/// uses the non-taxable product code that shares that same reason value, seeing it here can only mean
/// one thing: tax is owed and can't be collected. That's the one case this method escalates as
/// <see cref="TaxJurisdictionUnregisteredException"/> instead of returning a result - everything else
/// (<c>not_subject_to_tax</c>, <c>zero_rated</c>, <c>product_exempt</c>, <c>reverse_charge</c>, etc.) is
/// a legitimate zero and returns normally.
/// </para>
/// </remarks>
public class StripeTaxService(
    ApiDbContext dbContext, IOptions<StripeOptions> options, ILogger<StripeTaxService> logger)
    : IStripeTaxService
{
    /// <summary>
    /// "Software as a service (SaaS) - business use" - see this class's own remarks for why business,
    /// not personal, use.
    /// </summary>
    private const string TaxCode = "txcd_10103001";

    /// <inheritdoc />
    public async Task<StripeTaxResult?> CalculateAsync(
        Guid tenantId, decimal amount, string currency, string referenceId,
        CancellationToken cancellationToken = default)
    {
        var billingAddress = await dbContext.Set<TenantBillingAddress>()
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);

        if (billingAddress is null || string.IsNullOrEmpty(billingAddress.Country))
        {
            return null;
        }

        var requestOptions = new RequestOptions { ApiKey = options.Value.SecretKey };

        var calculationOptions = new Stripe.Tax.CalculationCreateOptions
        {
            Currency = currency.ToLowerInvariant(),
            CustomerDetails = new Stripe.Tax.CalculationCustomerDetailsOptions
            {
                Address = new AddressOptions
                {
                    Line1 = billingAddress.Line1,
                    Line2 = billingAddress.Line2,
                    City = billingAddress.City,
                    State = billingAddress.State,
                    PostalCode = billingAddress.PostalCode,
                    Country = billingAddress.Country
                },
                // "billing", not "shipping" - RustArchon ships nothing; the billing address is the
                // only address this tenant has ever given us, and it's what determines tax
                // jurisdiction for a service (as opposed to a physical good, where ship-to would
                // matter more).
                AddressSource = "billing"
            },
            LineItems =
            [
                new Stripe.Tax.CalculationLineItemOptions
                {
                    Amount = StripeAmounts.ToMinorUnits(amount),
                    TaxCode = TaxCode,
                    Reference = referenceId
                }
            ]
        };

        var calculation = await new Stripe.Tax.CalculationService()
            .CreateAsync(calculationOptions, requestOptions, cancellationToken);

        // Not collecting where tax is genuinely owed - see this class's own remarks. Checked before the
        // Transaction is ever created: there's nothing to remit yet, and creating one here would leave a
        // real Stripe Tax record behind for a sale that's about to be refused.
        var unregistered = calculation.TaxBreakdown
            .FirstOrDefault(b => b.TaxabilityReason == "not_collecting");
        if (unregistered is not null)
        {
            throw new TaxJurisdictionUnregisteredException(
                unregistered.TaxRateDetails?.Country ?? billingAddress.Country,
                unregistered.TaxRateDetails?.State ?? billingAddress.State);
        }

        var transactionOptions = new Stripe.Tax.TransactionCreateFromCalculationOptions
        {
            Calculation = calculation.Id,
            Reference = referenceId
        };

        var transaction = await new Stripe.Tax.TransactionService()
            .CreateFromCalculationAsync(transactionOptions, requestOptions, cancellationToken);

        var taxAmount = StripeAmounts.FromMinorUnits(calculation.TaxAmountExclusive);

        logger.LogInformation(
            "Calculated Stripe tax for tenant {TenantId}: {TaxAmount} {Currency} (transaction {TransactionId}).",
            tenantId, taxAmount, currency, transaction.Id);

        return new StripeTaxResult(taxAmount, transaction.Id);
    }

    /// <inheritdoc />
    public async Task ReverseAsync(
        string taxTransactionId, decimal amount, string referenceId,
        CancellationToken cancellationToken = default)
    {
        var requestOptions = new RequestOptions { ApiKey = options.Value.SecretKey };

        var reversalOptions = new Stripe.Tax.TransactionCreateReversalOptions
        {
            Mode = "partial",
            OriginalTransaction = taxTransactionId,
            Reference = referenceId,
            // Negative, per Stripe's own contract for flat_amount - it's "how much to remove," not "how
            // much remains."
            FlatAmount = -StripeAmounts.ToMinorUnits(amount)
        };

        var reversal = await new Stripe.Tax.TransactionService()
            .CreateReversalAsync(reversalOptions, requestOptions, cancellationToken);

        logger.LogInformation(
            "Reversed {Amount} of Stripe tax transaction {TransactionId} (reversal {ReversalId}).",
            amount, taxTransactionId, reversal.Id);
    }
}
