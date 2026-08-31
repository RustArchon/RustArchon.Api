// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="RconEvent"/> entities.
/// </summary>
public interface IRconEventRepository : IRepository<RconEvent>
{
    /// <summary>
    /// Gets one server's captured events, newest first, tenant-scoped like every other query on this
    /// entity (the global tenant query filter still applies - this just pre-filters to one server on
    /// top of it).
    /// </summary>
    Task<PagedResult<RconEvent>> GetForServerAsync(Guid rustServerId, QueryOptions<RconEvent> options);
}
