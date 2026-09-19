// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Api.Infrastructure.Captcha;

/// <summary>Verifies a Google reCAPTCHA token against its <c>siteverify</c> endpoint.</summary>
public class ReCaptchaVerifier(
    ILogger<ReCaptchaVerifier> logger, HttpClient httpClient, string secretKey) : ICaptchaVerifier
{
    private const string VerifyUrl = "https://www.google.com/recaptcha/api/siteverify";

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
                logger.LogWarning("reCAPTCHA verify call returned {StatusCode}.", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<ReCaptchaResponse>(cancellationToken);
            return result?.Success ?? false;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "reCAPTCHA verify call failed.");
            return false;
        }
    }

    private record ReCaptchaResponse([property: JsonPropertyName("success")] bool Success);
}
