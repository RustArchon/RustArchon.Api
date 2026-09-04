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

    // Matches RustServer.ConnectionStatusDetail's [MaxLength(200)] - see TryApplyConnectionStatusAsync's
    // remarks for why this exists at all.
    private const int ConnectionStatusDetailMaxLength = 200;

    /// <inheritdoc />
    public async Task<bool> TryApplyConnectionStatusAsync(
        Guid serverId, RconConnectionStatus status, string? detail, DateTimeOffset changedAtUtc)
    {
        // Truncated to fit the column - ExecuteUpdateAsync issues a raw UPDATE with no EF-side
        // validation of RustServer's [MaxLength(200)] attribute, so an over-length detail reached
        // Postgres directly and threw (22001: value too long for type character varying(200)),
        // unhandled, right here. Confirmed live: several servers with unreachable hosts/a bogus
        // hostname produced a real disconnect exception message (see RustWebRconClient.Socket_OnClose's
        // remarks on surfacing the innermost exception) long enough to trip this - and because this
        // call runs before ConnectionStatusConsumer ever gets to persist a ConnectionLogEntry, the
        // unhandled exception took the log entry down with it. ConnectionStatusDetail is only ever a
        // short glance-value anyway (the header badge's tooltip, the servers list) - the untruncated
        // detail still reaches ConnectionLogEntry.Message (an unbounded text column) once this no
        // longer throws before that write runs.
        var truncatedDetail = detail is { Length: > ConnectionStatusDetailMaxLength }
            ? string.Concat(detail.AsSpan(0, ConnectionStatusDetailMaxLength - 3), "...")
            : detail;

        var affected = await _dbSet
            .Where(server => server.Id == serverId
                && (server.ConnectionStatusChangedAtUtc == null || server.ConnectionStatusChangedAtUtc <= changedAtUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(server => server.ConnectionStatus, status)
                .SetProperty(server => server.ConnectionStatusDetail, truncatedDetail)
                .SetProperty(server => server.ConnectionStatusChangedAtUtc, changedAtUtc));

        return affected > 0;
    }
}
