// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Repositories;
using StackExchange.Redis;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// <see cref="IPlatformSettingsCache"/>, backed by Valkey via <see cref="IConnectionMultiplexer"/>
/// with Postgres (<see cref="IPlatformSettingRepository"/>) as the durable fallback.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Degrades gracefully at three levels</strong> - "we code defensively" applies to Valkey
/// itself being an optional accelerator, not a hard dependency the whole Api goes down without:
/// </para>
/// <list type="number">
/// <item>Valkey never configured at all (no <c>Valkey:ConnectionString</c>) - <c>Program.cs</c> simply
/// never registers <see cref="IConnectionMultiplexer"/>, <see cref="_redisFactory"/> below resolves
/// <c>null</c>, and every read/write here goes straight to Postgres.</item>
/// <item>Valkey configured but unreachable at Api startup - registered with
/// <c>AbortOnConnectFail = false</c> (see <c>Program.cs</c>), so building the multiplexer itself never
/// throws or blocks Api startup; it keeps retrying the connection in the background.</item>
/// <item>Valkey configured and normally reachable, but this one call fails - every Redis operation
/// below is wrapped in try/catch, logged once at Warning, and falls through to Postgres for that
/// request rather than surfacing a cache blip as a user-facing error.</item>
/// </list>
/// <para>
/// <see cref="IConnectionMultiplexer"/> is resolved lazily through <see cref="IServiceProvider"/>
/// rather than constructor-injected directly, specifically so case 1 above (Valkey never registered at
/// all) doesn't turn into a DI resolution failure merely for depending on this cache.
/// </para>
/// </remarks>
public class PlatformSettingsCache(
    IServiceProvider serviceProvider,
    IPlatformSettingRepository repository,
    ILogger<PlatformSettingsCache> logger) : IPlatformSettingsCache
{
    // 5 minutes: short enough that a missed SetAsync invalidation (a direct DB edit bypassing the
    // controller, a bug) self-heals quickly, long enough that the fast path is actually fast - this
    // is a defense-in-depth TTL, not the primary invalidation mechanism (SetAsync is).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const string KeyPrefix = "platform-setting:";

    private IConnectionMultiplexer? Redis => serviceProvider.GetService<IConnectionMultiplexer>();

    /// <inheritdoc />
    public async Task<bool> GetBooleanAsync(string key, bool defaultValue)
    {
        var raw = await GetStringAsync(key);
        if (raw is null)
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value)
    {
        var redis = Redis;
        if (redis is null)
        {
            return;
        }

        try
        {
            await redis.GetDatabase().StringSetAsync(CacheKeyFor(key), value, CacheTtl);
        }
        catch (RedisException ex)
        {
            // Not fatal - Postgres already has the new value (the controller writes there first), so
            // the very next read just falls through to GetStringAsync's own Postgres fallback below
            // and repopulates the cache then. Logged so a persistently-unreachable Valkey is visible
            // in the logs rather than silently degrading forever unnoticed.
            logger.LogWarning(ex, "Failed to write platform setting '{Key}' to Valkey - Postgres remains authoritative.", key);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetStringAsync(string key)
    {
        var redis = Redis;
        if (redis is not null)
        {
            try
            {
                var cached = await redis.GetDatabase().StringGetAsync(CacheKeyFor(key));
                if (cached.HasValue)
                {
                    return cached.ToString();
                }
            }
            catch (RedisException ex)
            {
                logger.LogWarning(ex, "Failed to read platform setting '{Key}' from Valkey - falling back to Postgres.", key);
            }
        }

        var setting = await repository.GetByKeyAsync(key);
        if (setting is null)
        {
            return null;
        }

        // Best-effort repopulation - a failure here just means the next read tries Valkey again and
        // falls back the same way; it must never turn a successful Postgres read into an error.
        if (redis is not null)
        {
            try
            {
                await redis.GetDatabase().StringSetAsync(CacheKeyFor(key), setting.Value, CacheTtl);
            }
            catch (RedisException)
            {
                // Already logged by the read attempt above in the common case; a write-only failure
                // here (read succeeded, write didn't) isn't worth a second warning for the same
                // underlying connectivity problem.
            }
        }

        return setting.Value;
    }

    private static string CacheKeyFor(string key) => KeyPrefix + key;
}
