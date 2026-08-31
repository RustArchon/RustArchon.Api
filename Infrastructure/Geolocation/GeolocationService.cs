// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure.Geolocation;

/// <summary>
/// Default <see cref="IGeolocationService"/> - dispatches to whichever registered
/// <see cref="IGeolocationProvider"/> matches the requested <see cref="GeolocationProviderKind"/>.
/// </summary>
public class GeolocationService(IEnumerable<IGeolocationProvider> providers, ILogger<GeolocationService> logger)
    : IGeolocationService
{
    /// <inheritdoc />
    public async Task<GeolocationResult?> LookupAsync(
        GeolocationProviderKind provider, string? apiKey, string ipAddress, CancellationToken cancellationToken = default)
    {
        if (provider == GeolocationProviderKind.None || string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var match = providers.FirstOrDefault(p => p.Kind == provider);
        if (match is null)
        {
            // Shouldn't happen in practice - every GeolocationProviderKind other than None has a
            // registered implementation (see Program.cs) - but a server's stored selection shouldn't
            // be able to throw just because DI wiring drifted from the enum somehow.
            logger.LogWarning("No IGeolocationProvider registered for {Provider}", provider);
            return null;
        }

        try
        {
            return await match.LookupAsync(apiKey, ipAddress, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort enrichment, not a critical path - a failing provider shouldn't fail
            // whatever triggered this lookup (see PlayerConnectedConsumer).
            logger.LogWarning(ex, "Geolocation provider {Provider} failed looking up {IpAddress}", provider, ipAddress);
            return null;
        }
    }
}
