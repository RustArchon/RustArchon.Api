// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure.Geolocation.Providers;

/// <summary>
/// STUB - ipinfo.io integration is not implemented yet.
/// </summary>
/// <remarks>
/// The real implementation calls ipinfo.io's <c>/{ip}?token={apiKey}</c> endpoint (its base "where is
/// this IP" data is free-tier; VPN/proxy detection specifically requires ipinfo's separate paid
/// "privacy detection" add-on - worth confirming that's actually wanted before wiring it in, since it's
/// a different pricing tier than the plain geolocation call), mapping <c>country</c>/<c>country_name</c>
/// (privacy detection: <c>privacy.vpn</c>/<c>privacy.proxy</c>) onto <see cref="GeolocationResult"/>.
/// See <see cref="ProxyCheckIoGeolocationProvider"/>'s remarks for why this always returns <c>null</c>
/// for now.
/// </remarks>
public class IpInfoIoGeolocationProvider(
    HttpClient httpClient,
    ILogger<IpInfoIoGeolocationProvider> logger) : IGeolocationProvider
{
    public GeolocationProviderKind Kind => GeolocationProviderKind.IpInfoIo;
    public string Name => "ipinfo.io";

    public Task<GeolocationResult?> LookupAsync(string apiKey, string ipAddress, CancellationToken cancellationToken)
    {
        logger.LogDebug("{Provider} is a stub - {IpAddress} was not actually looked up", Name, ipAddress);
        return Task.FromResult<GeolocationResult?>(null);
    }
}
