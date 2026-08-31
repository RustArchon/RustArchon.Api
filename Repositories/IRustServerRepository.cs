// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="RustServer"/> entities.
/// </summary>
public interface IRustServerRepository : IRepository<RustServer>
{
    /// <summary>
    /// Finds a server by name within the current tenant. Names are unique per tenant.
    /// </summary>
    Task<RustServer?> GetByNameAsync(string name);

    /// <summary>
    /// Finds an enabled, non-deleted server by id, regardless of which tenant owns it.
    /// </summary>
    /// <remarks>
    /// The only intentionally cross-tenant read in this codebase - a <c>RustArchon.Worker</c>
    /// instance asking "what are this server's connection details" has no tenant identity of its
    /// own to scope by. Gated entirely by <see cref="Controllers.InternalController"/>'s
    /// <c>InternalApiKey</c> authentication scheme, never reachable via a user/tenant credential.
    /// Returns <c>null</c> for a disabled server too, not just a deleted/missing one - a worker
    /// asking about a server it should no longer own is exactly the "tear the connection down" case,
    /// same as if it had been deleted.
    /// </remarks>
    Task<RustServer?> GetByIdAcrossTenantsAsync(Guid id);

    /// <summary>
    /// Finds every enabled, non-deleted server (across every tenant) whose heartbeat is missing or
    /// older than <paramref name="staleBefore"/> - the set <c>ServerClaimSweepService</c> re-publishes
    /// a fresh <c>RustArchon.Messaging.Contracts.ConnectToServer</c> claim for.
    /// </summary>
    Task<IReadOnlyList<RustServer>> GetServersNeedingClaimAsync(DateTimeOffset staleBefore);

    /// <summary>
    /// Atomically applies a connection-status transition, but only if it isn't older than whatever's
    /// already stored - a single UPDATE ... WHERE statement rather than a read-then-write, closing a
    /// real race where two transitions published moments apart (e.g. Connecting immediately followed
    /// by Connected) can be consumed concurrently on independently-read snapshots, letting whichever
    /// one happens to save last win regardless of which is actually newer - confirmed live this
    /// session as the cause of a status badge getting stuck on a stale value indefinitely even though
    /// the connection itself was fine.
    /// </summary>
    /// <returns>
    /// Whether the row was actually updated - <c>false</c> covers both "no such server" and "this
    /// transition is stale," which the caller doesn't need to tell apart (either way, nothing to
    /// relay).
    /// </returns>
    Task<bool> TryApplyConnectionStatusAsync(Guid serverId, RconConnectionStatus status, string? detail, DateTimeOffset changedAtUtc);
}
