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

    /// <inheritdoc />
    public async Task<bool> SetValueIfEmptyAsync(string key, string value)
    {
        var wrote = await _dbSet
            .Where(s => s.Key == key && s.Value == "")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, value)) == 1;

        // ExecuteUpdate goes straight to the database and bypasses the change tracker. If this context already
        // loaded the row (the caller read the setting just before, to see it was empty), a later GetByKeyAsync on
        // the same context would return that tracked copy with its OLD empty value - so the caller would conclude
        // nothing was stored. Refresh whatever is tracked, whether this call won the write or lost the race, so the
        // next read sees what is actually in the database.
        foreach (var entry in _context.ChangeTracker.Entries<PlatformSetting>().Where(e => e.Entity.Key == key).ToList())
        {
            await entry.ReloadAsync();
        }

        return wrote;
    }
}
