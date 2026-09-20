// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="ServerPluginStatus"/> entities.
/// </summary>
public class ServerPluginStatusRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<ServerPluginStatus>(context, userContext), IServerPluginStatusRepository
{
    /// <inheritdoc />
    public Task<ServerPluginStatus?> GetForServerAsync(Guid rustServerId) =>
        _dbSet.FirstOrDefaultAsync(s => s.RustServerId == rustServerId);

    /// <inheritdoc />
    public Task<ServerPluginStatus?> GetForServerAcrossTenantsAsync(Guid tenantId, Guid rustServerId) =>
        _dbSet.AcrossAllTenants()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.RustServerId == rustServerId);

    /// <inheritdoc />
    public async Task UpsertAsync(Guid tenantId, Guid rustServerId, ServerPluginStatus reported)
    {
        var existing = await GetForServerAcrossTenantsAsync(tenantId, rustServerId);

        if (existing is null)
        {
            reported.TenantId = tenantId;
            reported.RustServerId = rustServerId;
            await _dbSet.AddAsync(reported);
            await _context.SaveChangesAsync();
            return;
        }

        if (existing.CapturedAtUtc > reported.CapturedAtUtc)
        {
            return;
        }

        existing.ProtocolVersion = reported.ProtocolVersion;
        existing.PluginVersion = reported.PluginVersion;
        existing.Capabilities = reported.Capabilities;
        existing.ReportedRecordingEnabled = reported.ReportedRecordingEnabled;
        existing.ReportedCombatLogEnabled = reported.ReportedCombatLogEnabled;
        existing.SettingsPersisted = reported.SettingsPersisted;
        existing.SigningState = reported.SigningState;
        existing.SigningKeyFingerprint = reported.SigningKeyFingerprint;
        existing.CapturedAtUtc = reported.CapturedAtUtc;
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public Task<List<KeyReportSummary>> SummarizeByKeyAsync() =>
        _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(s => s.SigningKeyFingerprint != "")
            .GroupBy(s => s.SigningKeyFingerprint)
            .Select(g => new KeyReportSummary(g.Key, g.Count(), g.Max(s => s.CapturedAtUtc)))
            .ToListAsync();

    /// <inheritdoc />
    public async Task MarkSettingsAppliedAsync(Guid tenantId, Guid rustServerId, bool recordingEnabled, bool combatLogEnabled)
    {
        var existing = await GetForServerAcrossTenantsAsync(tenantId, rustServerId);
        if (existing is null)
        {
            return;
        }

        existing.ReportedRecordingEnabled = recordingEnabled;
        existing.ReportedCombatLogEnabled = combatLogEnabled;
        await _context.SaveChangesAsync();
    }
}
