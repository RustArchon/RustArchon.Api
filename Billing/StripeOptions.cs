// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Billing;

/// <summary>
/// Credentials for the Stripe account this deployment collects card payments through - see
/// <see cref="StripeCheckoutService"/>.
/// </summary>
/// <remarks>
/// Wired in <c>Program.cs</c> from flat <c>STRIPE_*</c> environment variables, the same convention as
/// every other third-party credential in this codebase (see <c>ObjectStorageOptions</c>'s own
/// <c>GARAGE_S3_*</c> wiring). Deliberately no <c>:?required</c> guard at startup the way the JWT/
/// internal-API-key secrets get - nothing at Api startup actually calls Stripe, so a deployment that
/// hasn't configured this yet still starts fine; only an actual "pay now" click or an incoming webhook
/// fails until it's set.
/// </remarks>
public class StripeOptions
{
    /// <summary>
    /// A restricted API key scoped to <c>Checkout Sessions: Write</c> and nothing else - see
    /// <see cref="StripeCheckoutService"/>'s remarks for why no broader scope is ever needed. Test-mode
    /// (<c>rk_test_...</c>) or live-mode (<c>rk_live_...</c>) depending on the deployment.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// The signing secret Stripe issues for one specific webhook endpoint - what
    /// <c>StripeWebhookController</c> verifies every inbound payload against before trusting anything
    /// in it. Not an API key; carries no ability to call Stripe's API at all, only to confirm a payload
    /// genuinely came from Stripe.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Where Checkout should send the browser back to once payment completes or is abandoned - the same
    /// value CORS already trusts as the Panel's own public URL (<c>CorsSettings:BlazorServerUrl</c>),
    /// read again here rather than threaded through as a second dependency purely because this is the
    /// one other place in the Api that needs to build a URL pointing at the Panel.
    /// </summary>
    public string PanelBaseUrl { get; set; } = string.Empty;
}
