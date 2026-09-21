// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>Repository interface for <see cref="PluginLoadFailure"/> entities.</summary>
public interface IPluginLoadFailureRepository : JumpStart.Repositories.IRepository<PluginLoadFailure>
{
    /// <summary>The reasons plugins on one server did not load, by file then position.</summary>
    Task<List<PluginLoadFailure>> GetForServerAsync(Guid rustServerId);

    /// <summary>
    /// Makes one server's stored rows match <paramref name="failures"/> exactly, in a single save; an empty collection clears them. A report captured
    /// earlier than what is stored is ignored, so two messages delivered out of order cannot bring back a failure that was already fixed.
    /// </summary>
    /// <remarks>Runs across all tenants - its only caller is the MassTransit consumer, which has no ambient tenant - and scopes every row by the ids on the message.</remarks>
    Task ReplaceForServerAsync(Guid tenantId, Guid rustServerId, IReadOnlyCollection<PluginLoadFailure> failures, DateTimeOffset capturedAtUtc);
}
