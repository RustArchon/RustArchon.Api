// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure.Geolocation.Providers;

/// <summary>
/// STUB - proxycheck.io integration is not implemented yet.
/// </summary>
/// <remarks>
/// Registered as a typed <see cref="HttpClient"/> client (see Program.cs) so the real implementation
/// is a drop-in: call proxycheck.io's <c>/v2/{ip}?key={apiKey}&amp;vpn=1</c> endpoint, map its
/// <c>country</c>/<c>isocode</c>/<c>proxy</c>/<c>type</c> fields onto <see cref="GeolocationResult"/>.
/// Until then this always returns <c>null</c>, which <see cref="IGeolocationService"/> treats as
/// "nothing to report" - functionally identical to this provider not existing at all. The API key now
/// arrives per-call (a server's own configured <see cref="Data.RustServer.GeolocationApiKey"/>), not
/// from injected options - see <see cref="IGeolocationProvider.LookupAsync"/>.
/// </remarks>
public class ProxyCheckIoGeolocationProvider(
    HttpClient httpClient,
    ILogger<ProxyCheckIoGeolocationProvider> logger) : IGeolocationProvider
{
    public GeolocationProviderKind Kind => GeolocationProviderKind.ProxyCheckIo;
    public string Name => "proxycheck.io";

    public Task<GeolocationResult?> LookupAsync(string apiKey, string ipAddress, CancellationToken cancellationToken)
    {
        logger.LogDebug("{Provider} is a stub - {IpAddress} was not actually looked up", Name, ipAddress);
        return Task.FromResult<GeolocationResult?>(null);
    }
}
