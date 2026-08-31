// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure.Geolocation;

/// <summary>
/// One IP-geolocation/VPN-detection service (proxycheck.io, iphub.info, ipinfo.io, ...). Every
/// implementation is registered side by side - see <see cref="IGeolocationService"/>, which dispatches
/// to whichever one a given <see cref="Data.RustServer"/> is actually configured to use, rather than
/// trying them all.
/// </summary>
/// <remarks>
/// The three implementations under <c>Providers/</c> are currently stubs - see each one's remarks.
/// They exist so this abstraction, its DI wiring, and the per-server provider-selection/API-key
/// settings built around it are real and exercised today, without pretending any of them actually
/// call out to a paid API yet.
/// </remarks>
public interface IGeolocationProvider
{
    /// <summary>Which <see cref="GeolocationProviderKind"/> this implementation handles - how
    /// <see cref="IGeolocationService"/> finds the right one for a given server's configured choice.</summary>
    GeolocationProviderKind Kind { get; }

    /// <summary>A short, stable name for this provider - stored on the session that used it, and used in logs.</summary>
    string Name { get; }

    /// <summary>
    /// Looks up <paramref name="ipAddress"/> using <paramref name="apiKey"/> (the calling server's own
    /// configured key for this provider - already decrypted). Returns <c>null</c> (never throws for an
    /// ordinary "no answer" case) when this lookup doesn't succeed for any reason -
    /// <see cref="IGeolocationService"/> treats <c>null</c> as "nothing to report," not as an error.
    /// </summary>
    Task<GeolocationResult?> LookupAsync(string apiKey, string ipAddress, CancellationToken cancellationToken);
}
