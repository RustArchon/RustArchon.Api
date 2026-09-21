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

/// <summary>Repository implementation for <see cref="PluginLoadFailure"/> entities.</summary>
public class PluginLoadFailureRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginLoadFailure>(context, userContext), IPluginLoadFailureRepository
{
    /// <inheritdoc />
    public Task<List<PluginLoadFailure>> GetForServerAsync(Guid rustServerId) =>
        _dbSet
            .Where(f => f.RustServerId == rustServerId)
            .OrderBy(f => f.FileName).ThenBy(f => f.Line).ThenBy(f => f.Column)
            .ToListAsync();

    /// <inheritdoc />
    public async Task ReplaceForServerAsync(
        Guid tenantId, Guid rustServerId, IReadOnlyCollection<PluginLoadFailure> failures, DateTimeOffset capturedAtUtc)
    {
        var existing = await _dbSet.AcrossAllTenants()
            .Where(f => f.TenantId == tenantId && f.RustServerId == rustServerId)
            .ToListAsync();

        if (existing.Any(f => f.CapturedAtUtc > capturedAtUtc))
        {
            return;
        }

        _dbSet.RemoveRange(existing);

        foreach (var failure in failures)
        {
            failure.TenantId = tenantId;
            failure.RustServerId = rustServerId;
            failure.CapturedAtUtc = capturedAtUtc;
        }

        await _dbSet.AddRangeAsync(failures);
        await _context.SaveChangesAsync();
    }
}
