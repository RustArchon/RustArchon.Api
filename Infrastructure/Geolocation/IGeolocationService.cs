// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure.Geolocation;

/// <summary>
/// Looks up an IP address against whichever single <see cref="IGeolocationProvider"/> matches the
/// caller's chosen <see cref="GeolocationProviderKind"/> - one specific provider per lookup, not a
/// try-every-one-in-order fallback, since the provider and its API key are now a per-server setting
/// (see <see cref="Data.RustServer.GeolocationProvider"/>/<see cref="Data.RustServer.GeolocationApiKey"/>),
/// not a fixed global configuration every server shares.
/// </summary>
public interface IGeolocationService
{
    /// <summary>
    /// Returns <c>null</c> immediately for <see cref="GeolocationProviderKind.None"/> or a missing
    /// <paramref name="apiKey"/>, and also (never throwing) if the matching provider fails or has no
    /// answer for <paramref name="ipAddress"/>.
    /// </summary>
    Task<GeolocationResult?> LookupAsync(
        GeolocationProviderKind provider, string? apiKey, string ipAddress, CancellationToken cancellationToken = default);
}
