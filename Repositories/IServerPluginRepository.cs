// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="ServerPlugin"/> entities.
/// </summary>
public interface IServerPluginRepository : JumpStart.Repositories.IRepository<ServerPlugin>
{
    /// <summary>
    /// Gets the plugins last reported for one server, ordered by name.
    /// </summary>
    Task<List<ServerPlugin>> GetForServerAsync(Guid rustServerId);

    /// <summary>
    /// The same list for a server whose organization is stated, not taken from the ambient tenant: what a background job needs, since it has
    /// none. Only rows of that organization are returned.
    /// </summary>
    Task<List<ServerPlugin>> GetForServerAcrossTenantsAsync(Guid tenantId, Guid rustServerId);

    /// <summary>
    /// Makes one server's stored plugin rows match <paramref name="plugins"/> exactly - rows for plugins
    /// no longer present are removed, new ones added, and everything else refreshed - in a single save.
    /// A report captured earlier than what is already stored is ignored, so two messages delivered out of
    /// order can't roll the list back to an older state.
    /// </summary>
    /// <remarks>
    /// Unlike every other member of this repository, this runs across all tenants - its only caller is
    /// the MassTransit consumer, which has no ambient tenant (see <c>PlayerSessionRepository</c>'s note on
    /// <c>AcrossAllTenants</c>). Every row it touches is scoped explicitly by the server id and tenant id
    /// on the message instead.
    /// </remarks>
    Task ReplaceForServerAsync(Guid tenantId, Guid rustServerId, IReadOnlyCollection<ServerPlugin> plugins, DateTimeOffset capturedAtUtc);
}
