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

public interface IPluginUpdateAttemptRepository : IRepository<PluginUpdateAttempt>
{
    /// <summary>Records that the server said it started an update.</summary>
    Task<PluginUpdateAttempt> RecordStartedAsync(
        Guid tenantId, Guid rustServerId, string kind, string fromVersion, string toVersion, string trigger, DateTimeOffset now);

    /// <summary>Records that the server's plugin turned an update request down.</summary>
    Task<PluginUpdateAttempt> RecordRefusedAsync(
        Guid tenantId, Guid rustServerId, string kind, string fromVersion, string toVersion, string trigger, string code, DateTimeOffset now);

    /// <summary>Attempts still waiting for their outcome, oldest first. Runs across tenants (the automatic updater is a platform job).</summary>
    Task<List<PluginUpdateAttempt>> GetPendingAsync(Guid rustServerId);

    /// <summary>Closes an attempt with its outcome; a no-op if it was already closed.</summary>
    Task ResolveAsync(Guid id, string state, string code, DateTimeOffset now);

    /// <summary>
    /// Whether this exact update (the kind of file and the version) has already been tried on this server and did not work out - it is
    /// pending, failed or was refused. A success does not count: if the version is installed there is nothing to update to.
    /// </summary>
    Task<bool> HasUnsuccessfulAsync(Guid rustServerId, string kind, string toVersion);

    /// <summary>The newest attempts for a server, newest first. Tenant-filtered.</summary>
    Task<List<PluginUpdateAttempt>> GetRecentForServerAsync(Guid rustServerId, int take);
}

public class PluginUpdateAttemptRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginUpdateAttempt>(context, userContext), IPluginUpdateAttemptRepository
{
    public async Task<PluginUpdateAttempt> RecordStartedAsync(
        Guid tenantId, Guid rustServerId, string kind, string fromVersion, string toVersion, string trigger, DateTimeOffset now)
    {
        var row = new PluginUpdateAttempt
        {
            TenantId = tenantId, RustServerId = rustServerId, Kind = kind, FromVersion = Cut(fromVersion, 50), ToVersion = Cut(toVersion, 50),
            Trigger = trigger, State = PluginUpdateAttemptStates.Started, StartedAtUtc = now
        };
        await _dbSet.AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
    }

    public async Task<PluginUpdateAttempt> RecordRefusedAsync(
        Guid tenantId, Guid rustServerId, string kind, string fromVersion, string toVersion, string trigger, string code, DateTimeOffset now)
    {
        var row = new PluginUpdateAttempt
        {
            TenantId = tenantId, RustServerId = rustServerId, Kind = kind, FromVersion = Cut(fromVersion, 50), ToVersion = Cut(toVersion, 50),
            Trigger = trigger, State = PluginUpdateAttemptStates.Refused, Code = Cut(code, 64), StartedAtUtc = now, ResolvedAtUtc = now
        };
        await _dbSet.AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
    }

    public Task<List<PluginUpdateAttempt>> GetPendingAsync(Guid rustServerId) =>
        _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(a => a.RustServerId == rustServerId && a.State == PluginUpdateAttemptStates.Started)
            .OrderBy(a => a.StartedAtUtc)
            .ToListAsync();

    public async Task ResolveAsync(Guid id, string state, string code, DateTimeOffset now) =>
        await _dbSet.AcrossAllTenants()
            .Where(a => a.Id == id && a.State == PluginUpdateAttemptStates.Started)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, state)
                .SetProperty(a => a.Code, Cut(code, 64))
                .SetProperty(a => a.ResolvedAtUtc, now));

    public Task<bool> HasUnsuccessfulAsync(Guid rustServerId, string kind, string toVersion) =>
        _dbSet.AcrossAllTenants().AsNoTracking().AnyAsync(a =>
            a.RustServerId == rustServerId && a.Kind == kind && a.ToVersion == toVersion && a.State != PluginUpdateAttemptStates.Succeeded);

    public Task<List<PluginUpdateAttempt>> GetRecentForServerAsync(Guid rustServerId, int take) =>
        _dbSet.AsNoTracking().Where(a => a.RustServerId == rustServerId).OrderByDescending(a => a.StartedAtUtc).Take(take).ToListAsync();

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
