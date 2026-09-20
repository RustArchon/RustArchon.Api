// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>Retired and revoked plugin signing keys. The active key is not here - see <see cref="PluginKeyHistory"/>.</summary>
public interface IPluginKeyHistoryRepository : IRepository<PluginKeyHistory>
{
    /// <summary>The retired or revoked key with this fingerprint, or <c>null</c>. Read fresh, never a tracked copy.</summary>
    Task<PluginKeyHistory?> GetByFingerprintAsync(string fingerprint);

    /// <summary>Every retired or revoked key, most recently retired first.</summary>
    Task<List<PluginKeyHistory>> ListAsync();
}

/// <inheritdoc cref="IPluginKeyHistoryRepository" />
public class PluginKeyHistoryRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginKeyHistory>(context, userContext), IPluginKeyHistoryRepository
{
    public Task<PluginKeyHistory?> GetByFingerprintAsync(string fingerprint) =>
        _dbSet.AsNoTracking().FirstOrDefaultAsync(k => k.Fingerprint == fingerprint);

    public Task<List<PluginKeyHistory>> ListAsync() =>
        _dbSet.AsNoTracking().OrderByDescending(k => k.RetiredAtUtc).ToListAsync();
}
