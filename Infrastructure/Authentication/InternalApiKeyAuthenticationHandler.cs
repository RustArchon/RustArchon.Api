// Copyright ©2026 Scott Blomfield

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RustArchon.Api.Infrastructure.Authentication;

/// <summary>
/// Authenticates service-to-service calls via a shared secret in the <c>X-Internal-Api-Key</c>
/// header, checked with a constant-time comparison so response timing can't be used to guess the
/// secret one byte at a time. See <see cref="InternalApiKeyOptions"/> for what's compared against and
/// why this is a separate scheme from the end-user JWT bearer scheme.
/// </summary>
/// <remarks>
/// On success, the resulting principal carries no <c>Permission</c> claims and is never meant to
/// satisfy <c>[EntityAuthorize]</c> or the <c>PlatformAdmin</c> policy - it authenticates "this call
/// came from one of our own trusted processes," nothing more. Registered in <c>Program.cs</c> as the
/// <c>"InternalApiKey"</c> scheme; endpoints opt in explicitly via
/// <c>[Authorize(AuthenticationSchemes = "InternalApiKey")]</c>.
/// </remarks>
public class InternalApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string HeaderName = "X-Internal-Api-Key";

    private readonly InternalApiKeyOptions _internalApiKeyOptions;

    public InternalApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<InternalApiKeyOptions> internalApiKeyOptions)
        : base(options, logger, encoder)
    {
        _internalApiKeyOptions = internalApiKeyOptions.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var providedKeyValues))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Missing {HeaderName} header."));
        }

        var providedKey = providedKeyValues.ToString();
        var expectedKey = _internalApiKeyOptions.SharedSecret;

        // FixedTimeEquals requires equal-length inputs, and byte length alone leaks nothing useful
        // about the secret - hashing both sides first only complicates this without adding any real
        // protection, so this compares the raw UTF-8 bytes directly.
        var isValid = !string.IsNullOrEmpty(expectedKey)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedKey),
                Encoding.UTF8.GetBytes(expectedKey));

        if (!isValid)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "internal-service")],
            Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
