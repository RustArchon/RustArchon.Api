// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RustArchon.Api.Infrastructure;

/// <inheritdoc cref="IAppGenerationCache" />
/// <remarks>
/// Mirrors <see cref="PlatformSettingsCache"/>'s degrade-gracefully shape:
/// <see cref="IConnectionMultiplexer"/> is resolved lazily through <see cref="IServiceProvider"/> so a
/// deployment that never configures <c>Valkey:ConnectionString</c> doesn't turn depending on this into
/// a DI resolution failure, and a Valkey write failure is logged and swallowed rather than surfacing as
/// an error - a missed bump just means open circuits keep whatever they already rendered a little
/// longer, never a broken request.
/// </remarks>
public class AppGenerationCache(IServiceProvider serviceProvider, ILogger<AppGenerationCache> logger)
    : IAppGenerationCache
{
    // No TTL - this key is meant to persist until the next bump, not expire on its own. Read by
    // RustArchon.Panel through the same key name via IValkeyCache.
    private const string Key = "app-generation";

    private IConnectionMultiplexer? Redis => serviceProvider.GetService<IConnectionMultiplexer>();

    /// <inheritdoc />
    public async Task BumpAsync()
    {
        var redis = Redis;
        if (redis is null)
        {
            // Valkey never configured - nothing to bump, and RustArchon.Panel's own read of this same
            // key degrades to "can't tell, don't force a reload" the same way, so this is a silent,
            // consistent no-op rather than a half-working feature.
            return;
        }

        try
        {
            // The value itself carries no meaning beyond "different from last time" - a fresh Guid
            // per bump is simplest, and never collides with whatever the previous generation was.
            await redis.GetDatabase().StringSetAsync(Key, Guid.NewGuid().ToString("N"));
        }
        catch (RedisException ex)
        {
            logger.LogWarning(
                ex, "Failed to bump the app generation in Valkey - open Panel circuits won't be told " +
                "to reload until their own state happens to change some other way.");
        }
    }
}
