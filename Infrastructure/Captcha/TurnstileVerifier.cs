// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Api.Infrastructure.Captcha;

/// <summary>Verifies a Cloudflare Turnstile token against its <c>siteverify</c> endpoint.</summary>
public class TurnstileVerifier(
    ILogger<TurnstileVerifier> logger, HttpClient httpClient, string secretKey) : ICaptchaVerifier
{
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public async Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["secret"] = secretKey,
            ["response"] = token
        };

        if (!string.IsNullOrEmpty(remoteIp))
        {
            fields["remoteip"] = remoteIp;
        }

        try
        {
            using var response = await httpClient.PostAsync(
                VerifyUrl, new FormUrlEncodedContent(fields), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile verify call returned {StatusCode}.", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<TurnstileResponse>(cancellationToken);
            return result?.Success ?? false;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Turnstile verify call failed.");
            return false;
        }
    }

    private record TurnstileResponse([property: JsonPropertyName("success")] bool Success);
}
