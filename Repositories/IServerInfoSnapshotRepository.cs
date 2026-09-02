// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="ServerInfoSnapshot"/> entities.
/// </summary>
public interface IServerInfoSnapshotRepository : JumpStart.Repositories.IRepository<ServerInfoSnapshot>
{
    /// <summary>
    /// Gets one server's snapshot history within an optional time range, oldest first - the order a
    /// chart wants to plot points in, unlike every other history endpoint in this project (which read
    /// newest first for a scrolling list).
    /// </summary>
    /// <remarks>
    /// Unpaged by design, unlike <c>IPlayerSessionRepository.GetForServerAsync</c> - a chart needs
    /// every point in the requested range at once, not a page of them. The controller still bounds the
    /// range itself (see <c>RustServersController.GetServerInfoHistory</c>'s <c>MaxServerInfoHistoryRange</c>)
    /// so this can't be asked to return an unbounded amount of data.
    /// </remarks>
    Task<List<ServerInfoSnapshot>> GetForServerAsync(Guid rustServerId, DateTimeOffset since, DateTimeOffset until);
}
