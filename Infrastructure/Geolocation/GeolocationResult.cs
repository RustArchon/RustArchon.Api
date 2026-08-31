// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.Geolocation;

/// <summary>
/// What an <see cref="IGeolocationProvider"/> reports about an IP address.
/// </summary>
/// <param name="Provider">
/// Which provider produced this result (e.g. "proxycheck.io") - stored alongside a
/// <c>PlayerSession</c>'s geolocation columns so a later change of provider/config doesn't leave old
/// and new results indistinguishable.
/// </param>
public record GeolocationResult(
    string Provider,
    string? Country,
    string? CountryCode,
    bool? IsVpn,
    bool? IsProxy);
