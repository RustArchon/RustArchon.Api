// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="ServerPlugin"/> entities.
/// </summary>
public class ServerPluginRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<ServerPlugin>(context, userContext), IServerPluginRepository
{
    /// <inheritdoc />
    public Task<List<ServerPlugin>> GetForServerAsync(Guid rustServerId) =>
        _dbSet
            .Where(p => p.RustServerId == rustServerId)
            .OrderBy(p => p.Name)
            .ToListAsync();

    /// <inheritdoc />
    public Task<List<ServerPlugin>> GetForServerAcrossTenantsAsync(Guid tenantId, Guid rustServerId) =>
        _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.RustServerId == rustServerId)
            .OrderBy(p => p.Name)
            .ToListAsync();

    /// <inheritdoc />
    public async Task ReplaceForServerAsync(
        Guid tenantId, Guid rustServerId, IReadOnlyCollection<ServerPlugin> plugins, DateTimeOffset capturedAtUtc)
    {
        var existing = await _dbSet.AcrossAllTenants()
            .Where(p => p.TenantId == tenantId && p.RustServerId == rustServerId)
            .ToListAsync();

        if (existing.Any(p => p.CapturedAtUtc > capturedAtUtc))
        {
            return;
        }

        _dbSet.RemoveRange(existing);

        foreach (var plugin in plugins)
        {
            plugin.TenantId = tenantId;
            plugin.RustServerId = rustServerId;
            plugin.CapturedAtUtc = capturedAtUtc;
        }

        await _dbSet.AddRangeAsync(plugins);
        await _context.SaveChangesAsync();
    }
}
