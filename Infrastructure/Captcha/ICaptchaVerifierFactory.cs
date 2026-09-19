// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;

namespace RustArchon.Api.Infrastructure.Captcha;

/// <summary>Resolves the <see cref="ICaptchaVerifier"/> matching the platform's current
/// <see cref="PlatformSettingsRegistry.CaptchaProvider"/> setting.</summary>
public interface ICaptchaVerifierFactory
{
    Task<ICaptchaVerifier> ResolveAsync();
}
