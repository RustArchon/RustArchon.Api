// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stripe;

namespace RustArchon.Api.Billing;

/// <summary>
/// Refunds part or all of a Stripe-collected payment, via Stripe's own Refund API.
/// </summary>
public interface IStripeRefundService
{
    /// <summary>
    /// Refunds <paramref name="amount"/> of the charge behind <paramref name="paymentIntentId"/>.
    /// </summary>
    /// <returns>Stripe's own id for the refund, once it has genuinely succeeded.</returns>
    /// <exception cref="InvalidOperationException">
    /// The refund did not resolve to <c>succeeded</c> synchronously - see this interface's own remarks
    /// for why that's treated as a hard failure rather than a "wait and see" state.
    /// </exception>
    Task<string> RefundAsync(
        string paymentIntentId, decimal amount, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IStripeRefundService" />
/// <remarks>
/// <strong>Assumes every refund resolves synchronously, and throws if one doesn't.</strong> Stripe's own
/// docs are explicit that a card refund - the only payment method <see cref="StripeCheckoutService"/>
/// currently ever collects through Checkout - completes synchronously and returns
/// <c>status: "succeeded"</c> in the same API response that creates it; only non-card methods
/// (bank transfers, some wallets) can come back <c>pending</c> and resolve later via a webhook. Since
/// this codebase collects nothing but cards today, there is no pending case to handle - and rather than
/// build (and leave untested) a webhook path for a state that can't currently occur, a
/// <c>pending</c>/<c>failed</c> result is treated as a hard failure here. The day a non-card payment
/// method is added, this is the class that needs the async webhook-confirmed path, not
/// <c>PaymentService</c>.
/// </remarks>
public class StripeRefundService(IStripeCredentialProvider credentials, ILogger<StripeRefundService> logger)
    : IStripeRefundService
{
    /// <inheritdoc />
    public async Task<string> RefundAsync(
        string paymentIntentId, decimal amount, CancellationToken cancellationToken = default)
    {
        var requestOptions = new RequestOptions { ApiKey = await credentials.GetSecretKeyAsync() };

        var refundOptions = new RefundCreateOptions
        {
            PaymentIntent = paymentIntentId,
            Amount = StripeAmounts.ToMinorUnits(amount)
        };

        var refund = await new RefundService().CreateAsync(refundOptions, requestOptions, cancellationToken);

        if (refund.Status != "succeeded")
        {
            // See this class's own remarks - every payment method this codebase collects through today
            // resolves synchronously, so anything other than "succeeded" here means something is
            // actually wrong (a card that no longer has funds, a Stripe-side failure), not a normal
            // "still processing" state to wait out.
            throw new InvalidOperationException(
                $"Stripe refund {refund.Id} for payment intent {paymentIntentId} did not succeed "
                + $"(status: {refund.Status}).");
        }

        logger.LogInformation(
            "Refunded {Amount} via Stripe for payment intent {PaymentIntentId} (refund {RefundId}).",
            amount, paymentIntentId, refund.Id);

        return refund.Id;
    }
}
