// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Api.Infrastructure.Captcha;

/// <summary>
/// Used while <see cref="PlatformSettingsRegistry.CaptchaProvider"/> is
/// <see cref="PlatformSettingsRegistry.CaptchaProviders.None"/> - every token passes. Fine for local
/// development or a deployment that isn't publicly reachable yet; leaving this in effect on a public
/// deployment removes the only defense the contact form has against scripted spam.
/// </summary>
public class NoOpCaptchaVerifier(ILogger<NoOpCaptchaVerifier> logger) : ICaptchaVerifier
{
    public Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken cancellationToken)
    {
        logger.LogDebug("Captcha verification skipped - no provider configured.");
        return Task.FromResult(true);
    }
}
