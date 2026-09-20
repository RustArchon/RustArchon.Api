// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="ServerPluginStatus"/> entities.
/// </summary>
public interface IServerPluginStatusRepository : JumpStart.Repositories.IRepository<ServerPluginStatus>
{
    /// <summary>
    /// Gets the last handshake reported for one server, or <c>null</c> if the plugin has never answered.
    /// Tenant-filtered like every ordinary read.
    /// </summary>
    Task<ServerPluginStatus?> GetForServerAsync(Guid rustServerId);

    /// <summary>
    /// Same as <see cref="GetForServerAsync"/> but across all tenants, scoped by both ids. For the MassTransit
    /// consumers, which have no ambient tenant (see <c>ServerPluginRepository.ReplaceForServerAsync</c>).
    /// </summary>
    Task<ServerPluginStatus?> GetForServerAcrossTenantsAsync(Guid tenantId, Guid rustServerId);

    /// <summary>
    /// Inserts or updates one server's status row from a handshake. A report captured earlier than what is
    /// already stored is ignored, so two messages delivered out of order cannot roll it back.
    /// </summary>
    /// <remarks>
    /// Runs across all tenants for the same reason as <see cref="GetForServerAcrossTenantsAsync"/>; the row is
    /// scoped explicitly by the server id and tenant id on the message.
    /// </remarks>
    Task UpsertAsync(Guid tenantId, Guid rustServerId, ServerPluginStatus reported);

    /// <summary>
    /// Records that the plugin was just told to switch to these values, so the stored "reported" state matches
    /// what the plugin now does without waiting for the next handshake. No-op if there is no row.
    /// </summary>
    Task MarkSettingsAppliedAsync(Guid tenantId, Guid rustServerId, bool recordingEnabled, bool combatLogEnabled);

    /// <summary>
    /// For each plugin signing key any server has reported, how many servers' latest handshake named it and when the
    /// latest of those was captured - across every tenant, since keys are platform-wide. Information for a platform
    /// admin only: an offline or lagging server reports nothing, so this can never prove a key is unused.
    /// </summary>
    Task<List<KeyReportSummary>> SummarizeByKeyAsync();
}

/// <summary>How many servers last reported a signing key, and when the latest report was captured.</summary>
public sealed record KeyReportSummary(string Fingerprint, int Servers, DateTimeOffset LastReportedUtc);
