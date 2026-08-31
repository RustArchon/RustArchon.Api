// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="PlatformSetting"/> entities.
/// </summary>
public class PlatformSettingRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PlatformSetting>(context, userContext), IPlatformSettingRepository
{
    /// <inheritdoc />
    public Task<PlatformSetting?> GetByKeyAsync(string key) =>
        _dbSet.FirstOrDefaultAsync(s => s.Key == key);
}
