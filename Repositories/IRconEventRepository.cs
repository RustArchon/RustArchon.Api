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
    /// <param name="isChat">
    /// When <c>true</c>, only frames whose <see cref="RconEvent.Type"/> is "Chat" (case-insensitive);
    /// when <c>false</c>, everything else; <c>null</c> (default) applies no type filter. Lets the
    /// Console and Chat tabs each page/filter independently against the same underlying stream.
    /// </param>
    /// <param name="since">When set, only events captured at or after this instant.</param>
    /// <param name="until">When set, only events captured at or before this instant.</param>
    Task<PagedResult<RconEvent>> GetForServerAsync(
        Guid rustServerId,
        QueryOptions<RconEvent> options,
        bool? isChat = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null);
}
