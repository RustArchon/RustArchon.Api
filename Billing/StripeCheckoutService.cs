// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;
using Stripe;
using Stripe.Checkout;
using Invoice = RustArchon.Api.Data.Invoice;

namespace RustArchon.Api.Billing;

/// <summary>
/// Starts a Stripe-hosted checkout for one of a tenant's own open invoices.
/// </summary>
public interface IStripeCheckoutService
{
    /// <summary>
    /// Creates a Stripe Checkout Session, in payment mode, for exactly <paramref name="invoiceId"/>'s
    /// <see cref="Invoice.AmountOutstanding"/> - see <see cref="StripeCheckoutService"/>'s remarks.
    /// </summary>
    /// <param name="tenantId">
    /// The caller's own tenant - the invoice must belong to it, or this returns <c>null</c> the same as
    /// a genuinely missing invoice. Never trust an invoice id alone; see this method's callers.
    /// </param>
    /// <returns>
    /// The hosted Checkout URL to redirect the browser to, or <c>null</c> if the invoice doesn't exist,
    /// doesn't belong to this tenant, isn't <see cref="InvoiceStatus.Open"/>, or is already settled.
    /// </returns>
    Task<string?> CreateCheckoutSessionAsync(
        Guid tenantId, Guid invoiceId, string successUrl, string cancelUrl,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IStripeCheckoutService" />
/// <remarks>
/// <para>
/// <strong>Payment mode only - never Stripe's own Subscriptions/Billing product.</strong> This
/// codebase's proration model (see <c>PlanChangeCalculator</c>'s remarks - independent plan/term/
/// quantity dials, each on its own immediate-or-deferred schedule, with a specific reconciliation
/// formula across repeated same-period changes) has no Stripe equivalent, so Stripe is used purely as a
/// payment rail for one already-priced invoice RustArchon's own billing engine computed - never as the
/// source of truth for what anything costs. A Checkout Session here carries exactly one line item, the
/// invoice's own <see cref="Invoice.AmountOutstanding"/> described by its number; Stripe never sees a
/// plan, a term, or a quantity.
/// </para>
/// <para>
/// <strong>Confirmation is asynchronous.</strong> This method's job ends at handing back a URL to
/// redirect to - it makes no assumption the payment actually completes. <c>StripeWebhookController</c>
/// is the only place <see cref="IPaymentService.RecordPaymentAsync"/> gets called from Stripe's side,
/// once Stripe's own webhook confirms the session actually completed.
/// </para>
/// </remarks>
public class StripeCheckoutService(
    ApiDbContext dbContext, IStripeCredentialProvider credentials, ILogger<StripeCheckoutService> logger)
    : IStripeCheckoutService
{
    /// <inheritdoc />
    public async Task<string?> CreateCheckoutSessionAsync(
        Guid tenantId, Guid invoiceId, string successUrl, string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        // Scoped to the caller's own tenant right here, not left to the controller - an invoice id
        // alone names nothing about who may check it out, and this is the one place that ever forms
        // the request Stripe acts on.
        var invoice = await dbContext.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, cancellationToken);

        if (invoice is null || invoice.Status != InvoiceStatus.Open || invoice.AmountOutstanding <= 0m)
        {
            return null;
        }

        var requestOptions = new RequestOptions { ApiKey = await credentials.GetSecretKeyAsync() };

        var sessionOptions = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            // Carried both ways on purpose: ClientReferenceId is Stripe's own general-purpose
            // correlation field (visible on the session itself), and Metadata is what
            // StripeWebhookController actually reads back - redundant, not load-bearing twice over,
            // but a webhook payload missing Metadata for some reason still has a second place to look.
            ClientReferenceId = invoiceId.ToString(),
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        // Stripe requires a lowercase ISO 4217 code; Invoice.Currency is stored
                        // uppercase (see its own remarks).
                        Currency = invoice.Currency.ToLowerInvariant(),
                        UnitAmount = StripeAmounts.ToMinorUnits(invoice.AmountOutstanding),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = $"Invoice {invoice.Number}"
                        }
                    }
                }
            ],
            Metadata = new Dictionary<string, string> { ["InvoiceId"] = invoiceId.ToString() },
            // Session-level Metadata (above) is NOT copied onto the underlying PaymentIntent by Stripe -
            // verified against Stripe's own docs, not assumed. Without this, a payment_intent.payment_failed
            // webhook (which carries the PaymentIntent, not the Session) would have no InvoiceId to act on.
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string> { ["InvoiceId"] = invoiceId.ToString() }
            }
        };

        var session = await new SessionService().CreateAsync(sessionOptions, requestOptions, cancellationToken);

        logger.LogInformation(
            "Created Stripe Checkout session {SessionId} for invoice {Number} ({Amount} {Currency}).",
            session.Id, invoice.Number, invoice.AmountOutstanding, invoice.Currency);

        return session.Url;
    }
}
