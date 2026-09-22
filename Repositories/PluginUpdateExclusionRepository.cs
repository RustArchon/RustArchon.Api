// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

public interface IPluginUpdateExclusionRepository : IRepository<PluginUpdateExclusion>
{
    /// <summary>The exclusion for this plugin on this server, or <c>null</c> if it is not excluded. Across tenants: the automatic pass is a platform job, and the caller has established the server.</summary>
    Task<PluginUpdateExclusion?> FindAsync(Guid rustServerId, string normalizedName);

    /// <summary>Every exclusion on this server, for building the offer list in one read instead of one per plugin.</summary>
    Task<List<PluginUpdateExclusion>> ListForServerAsync(Guid rustServerId);

    /// <summary>Excludes the plugin on the server, or updates the note on an exclusion that is already there.</summary>
    Task ExcludeAsync(Guid tenantId, Guid rustServerId, string normalizedName, string? note, DateTimeOffset now);

    /// <summary>Lifts the exclusion, if there is one.</summary>
    Task IncludeAsync(Guid rustServerId, string normalizedName);
}

public class PluginUpdateExclusionRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginUpdateExclusion>(context, userContext), IPluginUpdateExclusionRepository
{
    public async Task<PluginUpdateExclusion?> FindAsync(Guid rustServerId, string normalizedName) =>
        await _dbSet.AcrossAllTenants().AsNoTracking().FirstOrDefaultAsync(e => e.RustServerId == rustServerId && e.NormalizedName == normalizedName);

    public async Task<List<PluginUpdateExclusion>> ListForServerAsync(Guid rustServerId) =>
        await _dbSet.AcrossAllTenants().AsNoTracking().Where(e => e.RustServerId == rustServerId).ToListAsync();

    public async Task ExcludeAsync(Guid tenantId, Guid rustServerId, string normalizedName, string? note, DateTimeOffset now)
    {
        var trimmedNote = (note ?? string.Empty).Trim();
        var existing = await _dbSet.AcrossAllTenants().FirstOrDefaultAsync(e => e.RustServerId == rustServerId && e.NormalizedName == normalizedName);
        if (existing is null)
        {
            _dbSet.Add(new PluginUpdateExclusion { TenantId = tenantId, RustServerId = rustServerId, NormalizedName = normalizedName, Note = trimmedNote, ExcludedAtUtc = now });
        }
        else
        {
            existing.Note = trimmedNote;
            existing.ExcludedAtUtc = now;
        }

        await _context.SaveChangesAsync();
    }

    public async Task IncludeAsync(Guid rustServerId, string normalizedName) =>
        await _dbSet.AcrossAllTenants().Where(e => e.RustServerId == rustServerId && e.NormalizedName == normalizedName).ExecuteDeleteAsync();
}
