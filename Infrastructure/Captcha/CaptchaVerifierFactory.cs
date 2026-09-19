// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Infrastructure.Captcha;

/// <inheritdoc cref="ICaptchaVerifierFactory" />
/// <remarks>
/// <see cref="PlatformSettingsRegistry.CaptchaProvider"/> is read through <see cref="IPlatformSettingsCache"/>
/// like any other non-Secret setting; <see cref="PlatformSettingsRegistry.CaptchaSecretKey"/> is read
/// straight from Postgres and decrypted on the spot, the same "never in Valkey" rule every other Secret
/// setting follows - see <c>StripeCredentialProvider</c>'s remarks.
/// </remarks>
public class CaptchaVerifierFactory(
    IPlatformSettingsCache settingsCache,
    IPlatformSettingRepository settingRepository,
    IApiKeyProtector apiKeyProtector,
    IHttpClientFactory httpClientFactory,
    ILogger<ReCaptchaVerifier> reCaptchaLogger,
    ILogger<TurnstileVerifier> turnstileLogger,
    ILogger<NoOpCaptchaVerifier> noOpLogger) : ICaptchaVerifierFactory
{
    public async Task<ICaptchaVerifier> ResolveAsync()
    {
        var provider = await settingsCache.GetStringAsync(PlatformSettingsRegistry.CaptchaProvider);

        if (provider == PlatformSettingsRegistry.CaptchaProviders.ReCaptcha)
        {
            var secretKey = await DecryptSecretKeyAsync();
            if (!string.IsNullOrEmpty(secretKey))
            {
                return new ReCaptchaVerifier(reCaptchaLogger, httpClientFactory.CreateClient(), secretKey);
            }
        }

        if (provider == PlatformSettingsRegistry.CaptchaProviders.Turnstile)
        {
            var secretKey = await DecryptSecretKeyAsync();
            if (!string.IsNullOrEmpty(secretKey))
            {
                return new TurnstileVerifier(turnstileLogger, httpClientFactory.CreateClient(), secretKey);
            }
        }

        return new NoOpCaptchaVerifier(noOpLogger);
    }

    private async Task<string> DecryptSecretKeyAsync()
    {
        var entity = await settingRepository.GetByKeyAsync(PlatformSettingsRegistry.CaptchaSecretKey);
        return string.IsNullOrEmpty(entity?.Value)
            ? string.Empty
            : apiKeyProtector.Unprotect(ApiKeyProtectorPurposes.CaptchaSecretKey, entity.Value);
    }
}
