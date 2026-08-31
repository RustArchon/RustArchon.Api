// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

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
}
