// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="ConnectionLogEntry"/> entities.
/// </summary>
public interface IConnectionLogRepository : JumpStart.Repositories.IRepository<ConnectionLogEntry>
{
    /// <summary>
    /// Gets one server's Logs tab entries (connection-status transitions and worker-side diagnostics
    /// alike) within an optional time range, newest first - this is a diagnostic list a human scans
    /// top-down for "what just happened", not a chart, so it reads the opposite order from
    /// <see cref="IServerInfoSnapshotRepository.GetForServerAsync"/>.
    /// </summary>
    /// <remarks>
    /// Unpaged like <see cref="IServerInfoSnapshotRepository"/>, for the same reason: the controller
    /// bounds the range itself (see <c>RustServersController.GetConnectionLog</c>'s
    /// <c>MaxConnectionLogRange</c>), and these entries are low-volume enough (nothing like
    /// console/chat traffic) that a bounded range is never going to be an unreasonable amount of data.
    /// </remarks>
    Task<List<ConnectionLogEntry>> GetForServerAsync(Guid rustServerId, DateTimeOffset since, DateTimeOffset until);
}
