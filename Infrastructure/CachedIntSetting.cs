// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RustArchon.Api.Infrastructure;

/// <summary>Typed reads of Platform Settings that are whole numbers.</summary>
public static class PlatformSettingsCacheExtensions
{
    /// <summary>
    /// The setting as a whole number above zero, or <paramref name="fallback"/> when it is missing, not a number, or zero or less: a
    /// limit typed wrongly must never mean "nothing is allowed" or "no limit", so it means the default.
    /// </summary>
    public static async Task<int> GetPositiveInt32Async(this IPlatformSettingsCache cache, string key, int fallback)
    {
        var raw = await cache.GetStringAsync(key);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}

/// <summary>Reads a setting that may legitimately be zero (0 meaning "off" or "at once").</summary>
public static class PlatformSettingsCacheZeroExtensions
{
    /// <summary>The setting as a whole number of zero or more, or <paramref name="fallback"/> when it is missing, not a number, or negative.</summary>
    public static async Task<int> GetNonNegativeInt32Async(this IPlatformSettingsCache cache, string key, int fallback)
    {
        var raw = await cache.GetStringAsync(key);
        return int.TryParse(raw, out var value) && value >= 0 ? value : fallback;
    }
}

/// <summary>
/// A whole-number Platform Setting that code with no time to wait for a database read (a rate limiter deciding a request's fate) can read
/// at once. It answers from the last value it saw, and refreshes it in the background when that is more than <see cref="MaxAge"/> old.
/// </summary>
/// <remarks>
/// The very first read answers with the default and starts the first refresh; every later one has a real value. A failed refresh keeps
/// the old value and tries again on the next read, so a database blip never changes a limit.
/// </remarks>
public class CachedIntSetting(IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, string key, int fallback)
{
    /// <summary>How stale the value may get before a read starts a refresh. An admin's change takes effect within this.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly int _fallback = fallback;
    private int _value = fallback;
    private long _refreshedAtTicks;
    private int _refreshing;

    /// <summary>The current value, never waiting for the refresh it may start.</summary>
    public int Value
    {
        get
        {
            var age = clock.GetUtcNow().UtcTicks - Interlocked.Read(ref _refreshedAtTicks);
            if ((_refreshedAtTicks == 0 || age > MaxAge.Ticks) && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            {
                _ = RefreshAsync();
            }

            return Volatile.Read(ref _value);
        }
    }

    /// <summary>Refreshes now and returns when done. What the background refresh runs; also lets a test wait for it.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<IPlatformSettingsCache>();
            Volatile.Write(ref _value, await cache.GetPositiveInt32Async(key, _fallback));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the platform setting '{Key}'; keeping {Value}.", key, Volatile.Read(ref _value));
        }
        finally
        {
            // Stamped on failure too, so a database that is down is asked again in MaxAge, not on every request.
            Interlocked.Exchange(ref _refreshedAtTicks, clock.GetUtcNow().UtcTicks);
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }
}

/// <summary>The current limit on "Verify" checks of a third-party key per person per minute.</summary>
public class IntegrationCheckLimit(IServiceScopeFactory scopes, TimeProvider clock, ILogger<IntegrationCheckLimit> logger)
    : CachedIntSetting(scopes, clock, logger, PlatformSettingsRegistry.IntegrationChecksPerUserPerMinute, PlatformSettingsRegistry.DefaultIntegrationChecksPerUserPerMinute);
