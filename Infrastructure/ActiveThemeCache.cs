// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RustArchon.Api.Infrastructure;

/// <inheritdoc cref="IActiveThemeCache" />
/// <remarks>
/// Mirrors <see cref="AppGenerationCache"/>'s degrade-gracefully shape exactly: <see cref="IConnectionMultiplexer"/>
/// resolved lazily through <see cref="IServiceProvider"/> so a deployment that never configures
/// Valkey doesn't turn depending on this into a DI resolution failure, and a write failure is logged
/// and swallowed rather than surfacing as an error to whoever just clicked Activate - the activation
/// itself already succeeded in Postgres by the time this runs.
/// </remarks>
public class ActiveThemeCache(IServiceProvider serviceProvider, ILogger<ActiveThemeCache> logger)
    : IActiveThemeCache
{
    // No TTL - persists until the next activation. Read by RustArchon.Panel through this exact same
    // key name via IValkeyCache.
    private const string Key = "active-theme-id";

    private IConnectionMultiplexer? Redis => serviceProvider.GetService<IConnectionMultiplexer>();

    /// <inheritdoc />
    public async Task SetActiveAsync(Guid themeId)
    {
        var redis = Redis;
        if (redis is null)
        {
            return;
        }

        try
        {
            await redis.GetDatabase().StringSetAsync(Key, themeId.ToString("D"));
        }
        catch (RedisException ex)
        {
            logger.LogWarning(
                ex, "Failed to write the active theme id to Valkey - RustArchon.Panel won't pick up " +
                "this activation until Valkey is reachable again and something else repopulates it.");
        }
    }
}
