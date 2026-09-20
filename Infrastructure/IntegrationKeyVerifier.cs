// Copyright ©2026 Scott Blomfield

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Asks a third-party provider whether a key an admin typed is one it accepts, before the key is saved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fails closed.</b> <see cref="IntegrationKeyVerdict.Valid"/> is only ever returned on a positive signal - a successful answer
/// of the shape only an accepted key gets. A timeout, a rate limit, a status we do not recognise or a body we cannot read is never
/// "valid"; it is <see cref="IntegrationKeyVerdict.Unreachable"/>, <see cref="IntegrationKeyVerdict.RateLimited"/> or
/// <see cref="IntegrationKeyVerdict.Unknown"/>, so the wizard says "could not verify" rather than showing a green tick nobody earned.
/// </para>
/// <para>
/// Only the fixed provider addresses below are ever called, never one an admin supplies, so this cannot be turned into a way to make
/// the Api fetch arbitrary addresses. The key is sent to the provider and nowhere else: it is not stored and not logged (the typed
/// client is registered with its HTTP logging removed, since Steam only accepts the key in the address).
/// </para>
/// <para>
/// The lookups use a fixed, public IP address (a well-known DNS resolver) and a fixed, public Steam account, so no player data is
/// involved.
/// </para>
/// <para>
/// <b>What is and is not known about the providers.</b> Steam answers <c>403</c> to a bad key, which is well established. The
/// geolocation providers' own documentation does not say how they signal a bad key, so their checks lean on the positive signal
/// (a successful answer with the fields only an authorised call returns) plus the plain <c>401</c>/<c>403</c> meanings, and
/// proxycheck.io - which answers a successful call the same way for an unrecognised key as for none - can never be confirmed at all
/// and reports <see cref="IntegrationKeyVerdict.Unknown"/> on success. None of these have been run against the live providers.
/// </para>
/// </remarks>
public interface IIntegrationKeyVerifier
{
    Task<IntegrationKeyVerdict> VerifyAsync(
        IntegrationKeyKind kind, GeolocationProviderKind provider, string key, CancellationToken cancellationToken);
}

/// <inheritdoc />
public class IntegrationKeyVerifier(HttpClient httpClient, ILogger<IntegrationKeyVerifier> logger) : IIntegrationKeyVerifier
{
    // A long-standing public Steam account (Valve's own), so a lookup shows nothing about any RustArchon player.
    private const string SteamProbeId = "76561197960287930";

    // A well-known public DNS resolver: a fixed address that is nobody's.
    private const string ProbeIp = "8.8.8.8";

    /// <inheritdoc />
    public async Task<IntegrationKeyVerdict> VerifyAsync(
        IntegrationKeyKind kind, GeolocationProviderKind provider, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return IntegrationKeyVerdict.InvalidKey;
        }

        try
        {
            return kind switch
            {
                IntegrationKeyKind.SteamWebApi => await VerifySteamAsync(key, cancellationToken),
                IntegrationKeyKind.Geolocation => provider switch
                {
                    GeolocationProviderKind.IpHubInfo => await VerifyIpHubAsync(key, cancellationToken),
                    GeolocationProviderKind.IpInfoIo => await VerifyIpInfoAsync(key, cancellationToken),
                    GeolocationProviderKind.ProxyCheckIo => await VerifyProxyCheckAsync(key, cancellationToken),
                    _ => IntegrationKeyVerdict.Unknown
                },
                _ => IntegrationKeyVerdict.Unknown
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout fired: the provider did not answer.
            return IntegrationKeyVerdict.Unreachable;
        }
        catch (HttpRequestException ex)
        {
            // The message is logged, never the request - the address holds the key.
            logger.LogDebug("Verifying a {Kind} key could not reach the provider: {Reason}", kind, ex.Message);
            return IntegrationKeyVerdict.Unreachable;
        }
    }

    private async Task<IntegrationKeyVerdict> VerifySteamAsync(string key, CancellationToken cancellationToken)
    {
        var url = "https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/"
            + $"?key={Uri.EscapeDataString(key)}&steamids={SteamProbeId}";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        return await Classify(response, body => HasNonEmptyArray(body, "players"), cancellationToken);
    }

    private async Task<IntegrationKeyVerdict> VerifyIpHubAsync(string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://v2.api.iphub.info/ip/{ProbeIp}");
        request.Headers.Add("X-Key", key);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await Classify(response, body => HasProperties(body, "ip", "block"), cancellationToken);
    }

    private async Task<IntegrationKeyVerdict> VerifyIpInfoAsync(string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.ipinfo.io/lite/{ProbeIp}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await Classify(response, body => HasProperties(body, "ip", "country_code"), cancellationToken);
    }

    private async Task<IntegrationKeyVerdict> VerifyProxyCheckAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"https://proxycheck.io/v2/{ProbeIp}?key={Uri.EscapeDataString(key)}", cancellationToken);

        // A successful answer is identical for a good key and for one the provider does not recognise (it then treats the call as
        // unregistered), so there is no positive signal to find. Only the refusals mean anything.
        return await Classify(response, _ => false, cancellationToken);
    }

    /// <summary>
    /// Turns an HTTP answer into a verdict. <paramref name="isPositive"/> decides whether a successful body has the shape only an
    /// accepted key gets - and is the only route to <see cref="IntegrationKeyVerdict.Valid"/>.
    /// </summary>
    private static async Task<IntegrationKeyVerdict> Classify(
        HttpResponseMessage response, Func<string, bool> isPositive, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                return IntegrationKeyVerdict.InvalidKey;

            case HttpStatusCode.TooManyRequests:
                return IntegrationKeyVerdict.RateLimited;
        }

        if ((int)response.StatusCode >= 500)
        {
            return IntegrationKeyVerdict.Unreachable;
        }

        if (!response.IsSuccessStatusCode)
        {
            return IntegrationKeyVerdict.Unknown;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return isPositive(body) ? IntegrationKeyVerdict.Valid : IntegrationKeyVerdict.Unknown;
    }

    private static bool HasNonEmptyArray(string body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var array)
                && array.ValueKind == JsonValueKind.Array
                && array.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasProperties(string body, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (!document.RootElement.TryGetProperty(name, out _))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
