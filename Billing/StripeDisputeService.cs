// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace RustArchon.Api.Billing;

/// <summary>The evidence fields <see cref="IStripeDisputeService.SubmitEvidenceAsync"/> forwards to
/// Stripe - a small, typed subset of Stripe's own <c>Dispute.Evidence</c> shape (see
/// <see cref="ChargebackEvidenceService"/>'s remarks for why most of what this codebase can offer lands
/// in <see cref="UncategorizedText"/> rather than one of Stripe's more specific structured fields).</summary>
public sealed record DisputeEvidenceInput(
    string? CustomerEmailAddress,
    string? CustomerName,
    string? ProductDescription,
    string? ServiceDate,
    string? BillingAddress,
    string? UncategorizedText);

/// <summary>
/// Submits chargeback evidence to Stripe.
/// </summary>
public interface IStripeDisputeService
{
    /// <summary>
    /// Submits <paramref name="evidence"/> as the dispute's formal response.
    /// </summary>
    /// <remarks>
    /// <strong>One-shot, not a draft save.</strong> This always submits (Stripe's own
    /// <c>evidence.submit = true</c>) rather than only staging the fields for later review - Stripe
    /// itself still allows another update before <c>evidence_details.due_by</c> if it's genuinely
    /// needed, but the Panel's own confirmation dialog treats this as final, since there is no "undo" on
    /// a dispute response and a half-finished draft submitted by mistake can't be taken back from the
    /// card network's side.
    /// </remarks>
    Task SubmitEvidenceAsync(
        string disputeId, DisputeEvidenceInput evidence, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IStripeDisputeService" />
public class StripeDisputeService(IOptions<StripeOptions> options, ILogger<StripeDisputeService> logger)
    : IStripeDisputeService
{
    /// <inheritdoc />
    public async Task SubmitEvidenceAsync(
        string disputeId, DisputeEvidenceInput evidence, CancellationToken cancellationToken = default)
    {
        var requestOptions = new RequestOptions { ApiKey = options.Value.SecretKey };

        var updateOptions = new DisputeUpdateOptions
        {
            Evidence = new DisputeEvidenceOptions
            {
                CustomerEmailAddress = evidence.CustomerEmailAddress,
                CustomerName = evidence.CustomerName,
                ProductDescription = evidence.ProductDescription,
                ServiceDate = evidence.ServiceDate,
                BillingAddress = evidence.BillingAddress,
                UncategorizedText = evidence.UncategorizedText
            },
            Submit = true
        };

        await new DisputeService().UpdateAsync(disputeId, updateOptions, requestOptions, cancellationToken);

        logger.LogInformation("Submitted evidence for Stripe dispute {DisputeId}.", disputeId);
    }
}
