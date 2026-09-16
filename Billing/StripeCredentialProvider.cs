// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Billing;

/// <summary>
/// The Stripe secret key and webhook signing secret this deployment is currently configured with -
/// see <see cref="PlatformSettingsRegistry.StripeSecretKey"/>/
/// <see cref="PlatformSettingsRegistry.StripeWebhookSecret"/>, the only place either is declared.
/// </summary>
/// <remarks>
/// Every Stripe-calling service in this Api (<c>StripeCheckoutService</c>, <c>StripeTaxService</c>,
/// <c>StripeRefundService</c>, <c>StripeDisputeService</c>) reads through this rather than holding its
/// own copy of the lookup-and-decrypt logic - one place to get right, same as
/// <see cref="IApiKeyProtector"/> itself exists so no caller reinvents encryption. No
/// <c>CancellationToken</c> parameter - <see cref="IPlatformSettingRepository.GetByKeyAsync"/> doesn't
/// take one either, and accepting one here without actually being able to honor it would only be
/// misleading.
/// </remarks>
public interface IStripeCredentialProvider
{
    /// <summary>The API key to send Stripe requests under. Empty if never configured.</summary>
    Task<string> GetSecretKeyAsync();

    /// <summary>The signing secret to verify an inbound webhook payload against. Empty if never configured.</summary>
    Task<string> GetWebhookSecretAsync();
}

/// <inheritdoc cref="IStripeCredentialProvider" />
/// <remarks>
/// Reads straight from Postgres via <see cref="IPlatformSettingRepository"/> on every call, the same as
/// <c>InternalController.GetEmailSettings</c> does for the email provider's own secrets - never through
/// <see cref="IPlatformSettingsCache"/>, which deliberately never holds a Secret setting's value (see
/// <c>PlatformSettingsController.UpdateValue</c>'s own remarks for why: nothing needs ciphertext sitting
/// in Valkey when a Stripe call is rare enough that a Postgres round trip is not a hot path worth
/// protecting).
/// </remarks>
public class StripeCredentialProvider(
    IPlatformSettingRepository repository, IApiKeyProtector apiKeyProtector) : IStripeCredentialProvider
{
    /// <inheritdoc />
    public Task<string> GetSecretKeyAsync() =>
        DecryptAsync(PlatformSettingsRegistry.StripeSecretKey, ApiKeyProtectorPurposes.StripeSecretKey);

    /// <inheritdoc />
    public Task<string> GetWebhookSecretAsync() =>
        DecryptAsync(PlatformSettingsRegistry.StripeWebhookSecret, ApiKeyProtectorPurposes.StripeWebhookSecret);

    private async Task<string> DecryptAsync(string key, string purpose)
    {
        var entity = await repository.GetByKeyAsync(key);
        return string.IsNullOrEmpty(entity?.Value) ? string.Empty : apiKeyProtector.Unprotect(purpose, entity.Value);
    }
}
