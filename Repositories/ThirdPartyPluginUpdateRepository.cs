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

/// <summary>What is known about a third-party update when it is recorded: which plugin, from what to what, and the file that was checked.</summary>
public sealed record ThirdPartyUpdateRequest(
    Guid TenantId, Guid RustServerId, string PluginName, string NormalizedName, string ClassName, string FromVersion, string ToVersion,
    Guid PluginDownloadLookupId, string FileSha256, string Trigger, string Kind = "cs", bool SaveMapping = false);

public interface IThirdPartyPluginUpdateRepository : IRepository<ThirdPartyPluginUpdate>
{
    /// <summary>Records that the server's plugin said it started applying an update.</summary>
    Task<ThirdPartyPluginUpdate> RecordStartedAsync(ThirdPartyUpdateRequest request, DateTimeOffset now);

    /// <summary>Records that the server's plugin turned an update request down.</summary>
    Task<ThirdPartyPluginUpdate> RecordRefusedAsync(ThirdPartyUpdateRequest request, string code, string message, DateTimeOffset now);

    /// <summary>Updates still waiting for their outcome, oldest first. Across tenants: the automatic pass is a platform job.</summary>
    Task<List<ThirdPartyPluginUpdate>> GetPendingAsync(Guid rustServerId);

    /// <summary>Closes an update with its outcome; a no-op if it was already closed.</summary>
    Task ResolveAsync(Guid id, string state, string code, string message, string actualSha256, DateTimeOffset now);

    /// <summary>
    /// Whether this plugin's update to this version has already been tried on this server and did not work out - it is pending, failed, was refused, was
    /// rolled back, or found a changed file. A success does not count: if the version is installed there is nothing to update to.
    /// </summary>
    Task<bool> HasUnsuccessfulAsync(Guid rustServerId, string normalizedName, string toVersion);

    /// <summary>The newest update recorded for each plugin on a server, across tenants (the caller has already established who may see the server).</summary>
    Task<List<ThirdPartyPluginUpdate>> GetLatestPerPluginAsync(Guid rustServerId);
}

public class ThirdPartyPluginUpdateRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<ThirdPartyPluginUpdate>(context, userContext), IThirdPartyPluginUpdateRepository
{
    public async Task<ThirdPartyPluginUpdate> RecordStartedAsync(ThirdPartyUpdateRequest request, DateTimeOffset now)
    {
        var row = Build(request, ThirdPartyPluginUpdateStates.Started, string.Empty, string.Empty, now);
        await _dbSet.AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
    }

    public async Task<ThirdPartyPluginUpdate> RecordRefusedAsync(ThirdPartyUpdateRequest request, string code, string message, DateTimeOffset now)
    {
        var row = Build(request, ThirdPartyPluginUpdateStates.Refused, code, message, now);
        row.ResolvedAtUtc = now;
        await _dbSet.AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
    }

    public Task<List<ThirdPartyPluginUpdate>> GetPendingAsync(Guid rustServerId) =>
        _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(u => u.RustServerId == rustServerId && u.State == ThirdPartyPluginUpdateStates.Started)
            .OrderBy(u => u.StartedAtUtc)
            .ToListAsync();

    public async Task ResolveAsync(Guid id, string state, string code, string message, string actualSha256, DateTimeOffset now)
    {
        var shownCode = Cut(code, 64);
        var shownMessage = Clean(message, 500);
        var shownActual = Cut(actualSha256, 64);
        await _dbSet.AcrossAllTenants()
            .Where(u => u.Id == id && u.State == ThirdPartyPluginUpdateStates.Started)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.State, state)
                .SetProperty(u => u.Code, shownCode)
                .SetProperty(u => u.Message, shownMessage)
                .SetProperty(u => u.ActualSha256, shownActual)
                .SetProperty(u => u.ResolvedAtUtc, now));
    }

    public Task<bool> HasUnsuccessfulAsync(Guid rustServerId, string normalizedName, string toVersion) =>
        _dbSet.AcrossAllTenants().AsNoTracking().AnyAsync(u =>
            u.RustServerId == rustServerId && u.NormalizedName == normalizedName && u.ToVersion == toVersion && u.State != ThirdPartyPluginUpdateStates.Applied);

    public async Task<List<ThirdPartyPluginUpdate>> GetLatestPerPluginAsync(Guid rustServerId)
    {
        var all = await _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(u => u.RustServerId == rustServerId)
            .OrderByDescending(u => u.StartedAtUtc)
            .ToListAsync();
        return all.GroupBy(u => u.NormalizedName).Select(g => g.First()).ToList();
    }

    private static ThirdPartyPluginUpdate Build(ThirdPartyUpdateRequest r, string state, string code, string message, DateTimeOffset now) => new()
    {
        TenantId = r.TenantId,
        RustServerId = r.RustServerId,
        PluginName = Cut(r.PluginName, 200),
        NormalizedName = Cut(r.NormalizedName, 200),
        ClassName = Cut(r.ClassName, 100),
        FromVersion = Cut(r.FromVersion, 50),
        ToVersion = Cut(r.ToVersion, 50),
        PluginDownloadLookupId = r.PluginDownloadLookupId,
        FileSha256 = Cut(r.FileSha256, 64),
        Kind = r.Kind,
        SaveMapping = r.SaveMapping,
        Trigger = r.Trigger,
        State = state,
        Code = Cut(code, 64),
        Message = Clean(message, 500),
        StartedAtUtc = now
    };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    // What the server's plugin says is shown to a person: bounded, and with control characters (which could break a line or a log) turned into spaces.
    private static string Clean(string value, int max) =>
        Cut(new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), max);
}
