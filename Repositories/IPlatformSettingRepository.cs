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
}
