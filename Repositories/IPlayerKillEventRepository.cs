// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="PlayerKillEvent"/> entities.
/// </summary>
public interface IPlayerKillEventRepository : IRepository<PlayerKillEvent>
{
    /// <summary>
    /// Gets one server's kill history, newest first, tenant-scoped like every other query on this
    /// entity.
    /// </summary>
    Task<PagedResult<PlayerKillEvent>> GetForServerAsync(
        Guid rustServerId, QueryOptions<PlayerKillEvent> options, DateTimeOffset? since = null, DateTimeOffset? until = null);

    /// <summary>
    /// Gets every kill on this server involving this player - as victim or as killer - newest first.
    /// </summary>
    Task<PagedResult<PlayerKillEvent>> GetForPlayerAsync(Guid rustServerId, string steamId, int pageNumber, int pageSize);
}
