// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;

namespace RustArchon.Api.Infrastructure.Captcha;

/// <summary>
/// Checks a captcha token the contact form's widget produced client-side, before
/// <c>TicketSubmissionController</c> trusts an anonymous submission enough to create a
/// <see cref="Data.Ticket"/> from it. See <see cref="CaptchaVerifierFactory"/> for which
/// implementation a given deployment actually uses.
/// </summary>
public interface ICaptchaVerifier
{
    /// <summary>
    /// Whether <paramref name="token"/> is genuine. <paramref name="remoteIp"/> is passed through to
    /// the vendor's verify call when known (both reCAPTCHA and Turnstile accept it, purely as an extra
    /// signal on their end - never used for anything on this side).
    /// </summary>
    Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken cancellationToken);
}
