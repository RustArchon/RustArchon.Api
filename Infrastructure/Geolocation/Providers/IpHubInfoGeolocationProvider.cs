// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure.Geolocation.Providers;

/// <summary>
/// STUB - iphub.info integration is not implemented yet.
/// </summary>
/// <remarks>
/// The real implementation calls iphub.info's <c>/v2/info/{ip}</c> endpoint with an <c>X-Key</c>
/// header of the per-call <paramref name="apiKey"/>, mapping its <c>countryName</c>/<c>countryCode</c>/
/// <c>block</c> (0 = residential, 1 = known VPN/proxy/hosting, 2 = "risky") fields onto
/// <see cref="GeolocationResult"/>. See <see cref="ProxyCheckIoGeolocationProvider"/>'s remarks for
/// why this always returns <c>null</c> for now.
/// </remarks>
public class IpHubInfoGeolocationProvider(
    HttpClient httpClient,
    ILogger<IpHubInfoGeolocationProvider> logger) : IGeolocationProvider
{
    public GeolocationProviderKind Kind => GeolocationProviderKind.IpHubInfo;
    public string Name => "iphub.info";

    public Task<GeolocationResult?> LookupAsync(string apiKey, string ipAddress, CancellationToken cancellationToken)
    {
        logger.LogDebug("{Provider} is a stub - {IpAddress} was not actually looked up", Name, ipAddress);
        return Task.FromResult<GeolocationResult?>(null);
    }
}
