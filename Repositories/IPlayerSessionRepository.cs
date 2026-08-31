// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="PlayerSession"/> entities.
/// </summary>
public interface IPlayerSessionRepository : IRepository<PlayerSession>
{
    /// <summary>
    /// Gets one server's connect/disconnect history, newest-connection-first, tenant-scoped like every
    /// other query on this entity.
    /// </summary>
    Task<PagedResult<PlayerSession>> GetForServerAsync(
        Guid rustServerId, QueryOptions<PlayerSession> options, DateTimeOffset? since = null, DateTimeOffset? until = null);

    /// <summary>Sessions with no <see cref="PlayerSession.DisconnectedAtUtc"/> yet - i.e. currently connected.</summary>
    Task<List<PlayerSession>> GetCurrentlyConnectedAsync(Guid rustServerId);

    /// <summary>
    /// The most recent still-open session for this player on this server, if any - used to close out a
    /// session when a <c>PlayerDisconnected</c> event arrives.
    /// </summary>
    Task<PlayerSession?> GetOpenSessionAsync(Guid rustServerId, string steamId);

    /// <summary>
    /// Every still-open session for this player on this server - normally zero or one, but a Worker
    /// restart can leave more than one behind (its in-memory "who's already connected" state doesn't
    /// survive the restart, so the next reconciliation poll reports an already-online player as a
    /// fresh connect - see <c>ServerConnectionActor</c>'s remarks). Used by <c>PlayerConnectedConsumer</c>
    /// to close out any stale leftovers before opening a new session, so <see cref="GetCurrentlyConnectedAsync"/>
    /// never shows the same player more than once.
    /// </summary>
    Task<List<PlayerSession>> GetOpenSessionsAsync(Guid rustServerId, string steamId);

    /// <summary>
    /// One row per distinct player who has ever connected to this server but doesn't have an open
    /// session right now, combining their most recent session's details with their aggregate playtime
    /// across every session on record - newest-last-connection-first.
    /// </summary>
    /// <remarks>
    /// Aggregates in memory rather than via a fully server-side query - "latest row per group plus a
    /// summed duration across the group" doesn't translate cleanly through EF Core's LINQ provider,
    /// and a single Rust server's player history is small enough (tens to low thousands of sessions,
    /// not millions) that pulling it into memory once per request is a reasonable trade for
    /// straightforward code over a hand-written SQL aggregate. Paging happens after aggregation, for
    /// the same reason.
    /// </remarks>
    Task<PagedResult<InactivePlayerDto>> GetInactivePlayersAsync(Guid rustServerId, int pageNumber, int pageSize);

    /// <summary>
    /// One player's full summary on this server - everything <see cref="InactivePlayerDto"/> shows for
    /// an inactive player, plus whether they're currently connected. Returns <c>null</c> if this player
    /// has never had a session on this server at all (nothing to summarize).
    /// </summary>
    Task<PlayerDetailDto?> GetPlayerDetailAsync(Guid rustServerId, string steamId);

    /// <summary>One player's session history on this server, newest-connection-first.</summary>
    Task<PagedResult<PlayerSession>> GetSessionsForPlayerAsync(Guid rustServerId, string steamId, int pageNumber, int pageSize);
}
