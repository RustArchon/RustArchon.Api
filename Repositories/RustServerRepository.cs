// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="RustServer"/> entities.
/// </summary>
public class RustServerRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<RustServer>(context, userContext), IRustServerRepository
{
    /// <inheritdoc />
    public async Task<RustServer?> GetByNameAsync(string name)
    {
        return await _dbSet.FirstOrDefaultAsync(server => server.Name == name);
    }

    /// <inheritdoc />
    public async Task<RustServer?> GetByIdAcrossTenantsAsync(Guid id)
    {
        // IgnoreQueryFilters() strips both the tenant-scoping filter and the soft-delete filter that
        // every other query on this DbSet gets automatically - the DeletedOn check has to be added
        // back by hand, same pattern as the one other intentionally-cross-tenant read in this codebase
        // (InvitationCodeRepository.TryRedeemAsync).
        return await _dbSet
            .IgnoreQueryFilters()
            .Where(server => server.DeletedOn == null && server.IsEnabled)
            .FirstOrDefaultAsync(server => server.Id == id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RustServer>> GetServersNeedingClaimAsync(DateTimeOffset staleBefore)
    {
        // IgnoreQueryFilters() for the same reason as GetByIdAcrossTenantsAsync above - this sweep
        // spans every tenant by design, not just whichever one (if any) happens to be ambient.
        return await _dbSet
            .IgnoreQueryFilters()
            .Where(server => server.DeletedOn == null
                && server.IsEnabled
                && (server.LastHeartbeatUtc == null || server.LastHeartbeatUtc < staleBefore))
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> TryApplyConnectionStatusAsync(
        Guid serverId, RconConnectionStatus status, string? detail, DateTimeOffset changedAtUtc)
    {
        var affected = await _dbSet
            .Where(server => server.Id == serverId
                && (server.ConnectionStatusChangedAtUtc == null || server.ConnectionStatusChangedAtUtc <= changedAtUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(server => server.ConnectionStatus, status)
                .SetProperty(server => server.ConnectionStatusDetail, detail)
                .SetProperty(server => server.ConnectionStatusChangedAtUtc, changedAtUtc));

        return affected > 0;
    }
}
