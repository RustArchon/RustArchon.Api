// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="PlatformSetting"/> entities.
/// </summary>
public interface IPlatformSettingRepository : IRepository<PlatformSetting>
{
    /// <summary>
    /// Gets a setting by its unique <see cref="PlatformSetting.Key"/> rather than its <c>Id</c> -
    /// the lookup every reader (the cache's fallback path, the seeder's idempotency check) actually
    /// needs, since callers know the well-known key, never the row's Guid.
    /// </summary>
    Task<PlatformSetting?> GetByKeyAsync(string key);

    /// <summary>
    /// Sets a setting's stored value <b>only if it is currently empty</b>, as one atomic statement, and says whether
    /// this call is the one that set it. For a value that is generated once and must never be overwritten by a
    /// racing second writer - the plugin signing key: two instances handling the first download at the same moment
    /// must not end up with two different keys, one of which has already signed a script.
    /// </summary>
    /// <param name="key">The setting's <see cref="PlatformSetting.Key"/>.</param>
    /// <param name="value">The value exactly as it is to be stored (already encrypted, for a Secret setting).</param>
    /// <returns><c>true</c> if this call wrote the value; <c>false</c> if the setting already had one (or does not exist).</returns>
    Task<bool> SetValueIfEmptyAsync(string key, string value);
}
