// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="ServerReport"/> entities.
/// </summary>
public interface IServerReportRepository : IRepository<ServerReport>
{
    /// <summary>
    /// Gets one server's reports, newest first, tenant-scoped like every other query on this entity.
    /// </summary>
    Task<PagedResult<ServerReport>> GetForServerAsync(
        Guid rustServerId, int pageNumber, int pageSize,
        ServerReportType? type = null, ServerReportStatus? status = null, string? targetSteamId = null);

    /// <summary>How many of one server's reports are still <see cref="ServerReportStatus.New"/>. Tenant-scoped.</summary>
    Task<int> CountNewAsync(Guid rustServerId);

    /// <summary>When the last report that arrived by the native route was received, or <c>null</c> if none has. Tenant-scoped.</summary>
    Task<DateTimeOffset?> GetLastNativeReceivedAsync(Guid rustServerId);

    /// <summary>
    /// Finds the report a newly arrived one is a second copy of, if any - same server, same reporter, same type, same subject,
    /// received at or after <paramref name="since"/>, and not already carrying <paramref name="incomingSource"/> (a route does not
    /// merge into itself). Deliberately crosses the tenant boundary: it is called from ingestion, which has no ambient tenant
    /// (the sender is a game server, not a user), and it is scoped by server id instead.
    /// </summary>
    Task<ServerReport?> FindMergeCandidateAcrossTenantsAsync(
        Guid rustServerId, string? reporterSteamId, ServerReportType type, string subject,
        DateTimeOffset since, ServerReportSource incomingSource);

    /// <summary>
    /// Removes every report of a server, as part of removing the server. Crosses the tenant boundary because its only caller is a bus
    /// consumer with no ambient tenant; it is scoped by the server id the message named, so it can only ever touch that server.
    /// </summary>
    Task<int> DeleteForServerAcrossTenantsAsync(Guid rustServerId);
}
