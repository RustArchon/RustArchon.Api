// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

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
        // Deliberately crosses the tenant boundary - a worker claiming a connection has no ambient
        // tenant. AcrossAllTenants drops only the tenant filter, so soft-deleted servers stay hidden
        // without a hand-written DeletedOn check (which is what this used to need, and what every
        // cross-tenant read in this codebase used to have to remember).
        return await _dbSet
            .AcrossAllTenants()
            .Where(server => server.IsEnabled)
            .FirstOrDefaultAsync(server => server.Id == id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RustServer>> GetServersNeedingClaimAsync(DateTimeOffset staleBefore)
    {
        // Same reason as GetByIdAcrossTenantsAsync above - this sweep spans every tenant by design,
        // not just whichever one (if any) happens to be ambient.
        //
        // Suspended and cancelled Organizations are excluded, and this is the only thing that makes
        // suspension hold. Suspending publishes a teardown per server, but every server it just stopped
        // still has IsEnabled true (deliberately - that field is the customer's intent, see
        // IOrganizationLifecycleService) and a stale heartbeat, which is precisely this query's
        // definition of "needs claiming". Without the exclusion the sweep would hand every suspended
        // connection straight back within one interval, and the suspension would appear to work for
        // about a minute.
        return await context.Set<RustServer>()
            .AcrossAllTenants()
            .Where(server => server.IsEnabled
                && (server.LastHeartbeatUtc == null || server.LastHeartbeatUtc < staleBefore)
                && !context.Set<Subscription>().Any(s =>
                    s.TenantId == server.TenantId
                    && s.EndDate == null
                    && (s.Status == SubscriptionStatus.Suspended || s.Status == SubscriptionStatus.Cancelled)))
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
