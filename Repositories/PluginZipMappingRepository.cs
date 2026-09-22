// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Api.Repositories;

/// <summary>A saved mapping with its rules read out.</summary>
public sealed record SavedZipMapping(Guid Id, IReadOnlyList<ZipMappingRule> Rules, bool Trusted, DateTimeOffset SavedAtUtc);

public interface IPluginZipMappingRepository : IRepository<PluginZipMapping>
{
    /// <summary>The instructions saved for this plugin on this server, or <c>null</c>. Across tenants: the automatic pass is a platform job, and the caller has established the server.</summary>
    Task<SavedZipMapping?> FindAsync(Guid rustServerId, string normalizedName);

    /// <summary>
    /// Saves these rules for the plugin on the server, replacing what was there. Not trusted: the rules that were given are only trusted once an update
    /// applied with them has come up loaded. Rules identical to the ones already saved keep their trust.
    /// </summary>
    Task SaveAsync(Guid tenantId, Guid rustServerId, string normalizedName, IReadOnlyList<ZipMappingRule> rules, DateTimeOffset now);

    /// <summary>Marks the saved rules as having produced a working update.</summary>
    Task MarkTrustedAsync(Guid rustServerId, string normalizedName, DateTimeOffset now);

    /// <summary>Forgets the instructions for the plugin on the server.</summary>
    Task DeleteAsync(Guid rustServerId, string normalizedName);
}

public class PluginZipMappingRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginZipMapping>(context, userContext), IPluginZipMappingRepository
{
    public async Task<SavedZipMapping?> FindAsync(Guid rustServerId, string normalizedName)
    {
        var row = await _dbSet.AcrossAllTenants().AsNoTracking().FirstOrDefaultAsync(m => m.RustServerId == rustServerId && m.NormalizedName == normalizedName);
        return row is null ? null : new SavedZipMapping(row.Id, ParseRules(row.RulesJson), row.Trusted, row.SavedAtUtc);
    }

    public async Task SaveAsync(Guid tenantId, Guid rustServerId, string normalizedName, IReadOnlyList<ZipMappingRule> rules, DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(rules);
        var existing = await _dbSet.AcrossAllTenants().FirstOrDefaultAsync(m => m.RustServerId == rustServerId && m.NormalizedName == normalizedName);
        if (existing is null)
        {
            _dbSet.Add(new PluginZipMapping { TenantId = tenantId, RustServerId = rustServerId, NormalizedName = normalizedName, RulesJson = json, Trusted = false, SavedAtUtc = now });
        }
        else if (existing.RulesJson != json)
        {
            existing.RulesJson = json;
            existing.Trusted = false;
            existing.SavedAtUtc = now;
        }

        await _context.SaveChangesAsync();
    }

    public async Task MarkTrustedAsync(Guid rustServerId, string normalizedName, DateTimeOffset now) =>
        await _dbSet.AcrossAllTenants()
            .Where(m => m.RustServerId == rustServerId && m.NormalizedName == normalizedName)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Trusted, true).SetProperty(m => m.LastAppliedAtUtc, now));

    public async Task DeleteAsync(Guid rustServerId, string normalizedName) =>
        await _dbSet.AcrossAllTenants().Where(m => m.RustServerId == rustServerId && m.NormalizedName == normalizedName).ExecuteDeleteAsync();

    /// <summary>The rules in stored JSON; none if it cannot be read (a mapping nobody can read is a mapping that is not used).</summary>
    private static List<ZipMappingRule> ParseRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ZipMappingRule>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
