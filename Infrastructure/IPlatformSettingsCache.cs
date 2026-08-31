// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Fast, typed access to <see cref="Data.PlatformSetting"/> values - the thing every request that
/// needs to check a platform setting (e.g. "are invitation codes required?") calls, instead of
/// querying Postgres directly on every request.
/// </summary>
/// <remarks>
/// Backed by Valkey (see <see cref="PlatformSettingsCache"/>) with Postgres as the durable fallback -
/// callers never need to know or care whether a given read was a cache hit.
/// </remarks>
public interface IPlatformSettingsCache
{
    /// <summary>Gets a <see cref="Data.PlatformSettingValueType.Boolean"/> setting's current value.</summary>
    /// <param name="key">The setting's <see cref="Data.PlatformSetting.Key"/>.</param>
    /// <param name="defaultValue">
    /// Returned if the setting doesn't exist yet (should only happen if
    /// <see cref="PlatformSettingsRegistry"/> hasn't seeded it) or its value fails to parse as a bool.
    /// </param>
    Task<bool> GetBooleanAsync(string key, bool defaultValue);

    /// <summary>
    /// Writes a setting's new value through to the cache immediately - called by
    /// <see cref="Controllers.PlatformSettingsController"/> right after persisting the same value to
    /// Postgres, so every other request sees the change on its very next read instead of waiting for
    /// a cache entry to expire.
    /// </summary>
    Task SetAsync(string key, string value);
}
