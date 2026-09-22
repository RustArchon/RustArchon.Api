// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Services;

namespace RustArchon.Api.Repositories;

public interface IPluginDownloadLookupRepository : IRepository<PluginDownloadLookup>
{
    /// <summary>
    /// What still has to be asked: each distinct plugin, marketplace and version among the update notices held (across every organization)
    /// that has never been asked about, or whose last answer was not a settled one and is due again. Most overdue first, at most <paramref name="limit"/>.
    /// </summary>
    Task<List<PluginDownloadRequest>> GetDueAsync(DateTimeOffset now, int limit);

    /// <summary>The row for this plugin, marketplace and version, or <c>null</c> if it has never been asked.</summary>
    Task<PluginDownloadLookup?> FindAsync(string marketplaceKey, string normalizedName, string versionKey);

    /// <summary>Every <see cref="PluginDownloadOutcome.Found"/> row for these plugin names (any marketplace or version); the caller picks its own.</summary>
    Task<List<PluginDownloadLookup>> GetFoundForNamesAsync(IReadOnlyCollection<string> normalizedNames);

    /// <summary>Stores the row, replacing what is held for the same plugin, marketplace and version. Two servers finishing the same ask at once is not an error.</summary>
    Task SaveAsync(PluginDownloadLookup row);

    /// <summary>The row with this id, or <c>null</c>.</summary>
    Task<PluginDownloadLookup?> FindByIdAsync(Guid id);

    /// <summary>
    /// The found downloads whose file has still to be looked at - never checked, or a failed check that is due again - but only those somebody
    /// is waiting on: a plugin with an update notice on a server that has opted in to automatic third-party updates, on a plan that offers them.
    /// Nothing is downloaded on the strength of a plugin nobody would apply. Longest waiting first, at most <paramref name="limit"/>.
    /// </summary>
    Task<List<PluginDownloadLookup>> GetDueForValidationAsync(DateTimeOffset now, int limit);

    /// <summary>Stores only what was learned about the file (the validation fields) on the row with this id, leaving the lookup's own answer as it is.</summary>
    Task SaveValidationAsync(PluginDownloadLookup row);
}

public class PluginDownloadLookupRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginDownloadLookup>(context, userContext), IPluginDownloadLookupRepository
{
    public async Task<List<PluginDownloadRequest>> GetDueAsync(DateTimeOffset now, int limit)
    {
        // Every organization's notices: this is a platform job, and the answer is shared. Small - a notice per plugin with an update, per server.
        var notices = await _context.Set<PluginUpdateNotice>().AcrossAllTenants().AsNoTracking()
            .Where(n => n.Marketplace != "" && n.LatestVersion != "")
            .Select(n => new { n.Name, n.Marketplace, n.LatestVersion, n.Url })
            .ToListAsync();

        var wanted = notices
            .Select(n => new PluginDownloadRequest(n.Name, n.Marketplace, n.LatestVersion, n.Url))
            .Where(r => r.MarketplaceKey.Length > 0 && r.NormalizedName.Length > 0 && r.VersionKey.Length > 0)
            .GroupBy(r => (r.MarketplaceKey, r.NormalizedName, r.VersionKey))
            .Select(g => g.First())
            .ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var names = wanted.Select(r => r.NormalizedName).Distinct().ToList();
        var held = (await _dbSet.AsNoTracking().Where(l => names.Contains(l.NormalizedName)).ToListAsync())
            .ToDictionary(l => (l.MarketplaceKey, l.NormalizedName, l.Version));

        var due = new List<(PluginDownloadRequest Request, DateTimeOffset Since)>();
        foreach (var request in wanted)
        {
            if (!held.TryGetValue((request.MarketplaceKey, request.NormalizedName, request.VersionKey), out var row))
            {
                due.Add((request, DateTimeOffset.MinValue));   // never asked: first
            }
            else if (row.Outcome != PluginDownloadOutcome.Found && row.NextAttemptUtc is { } next && next <= now)
            {
                due.Add((request, next));
            }
        }

        return due.OrderBy(d => d.Since).Take(limit).Select(d => d.Request).ToList();
    }

    public Task<PluginDownloadLookup?> FindAsync(string marketplaceKey, string normalizedName, string versionKey) =>
        _dbSet.AsNoTracking().FirstOrDefaultAsync(l => l.MarketplaceKey == marketplaceKey && l.NormalizedName == normalizedName && l.Version == versionKey);

    public async Task<List<PluginDownloadLookup>> GetFoundForNamesAsync(IReadOnlyCollection<string> normalizedNames)
    {
        if (normalizedNames.Count == 0)
        {
            return [];
        }

        var names = normalizedNames.ToList();
        return await _dbSet.AsNoTracking()
            .Where(l => l.Outcome == PluginDownloadOutcome.Found && names.Contains(l.NormalizedName))
            .ToListAsync();
    }

    public Task<PluginDownloadLookup?> FindByIdAsync(Guid id) => _dbSet.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);

    public async Task<List<PluginDownloadLookup>> GetDueForValidationAsync(DateTimeOffset now, int limit)
    {
        // Who is waiting: the plugins (by name) with an outstanding notice on a server that opted in, whose organization's plan offers the feature.
        // By name only - the rows below are then matched to a notice's marketplace and version by the job itself being asked for that row.
        var wanted = await (
            from notice in _context.Set<PluginUpdateNotice>().AcrossAllTenants()
            join server in _context.Set<RustServer>().AcrossAllTenants() on notice.RustServerId equals server.Id
            where server.IsEnabled && server.ThirdPartyPluginUpdatesEnabled
                && _context.Set<Subscription>().IgnoreQueryFilters()
                    .Any(s => s.TenantId == server.TenantId && s.EndDate == null && s.Plan.OffersThirdPartyPluginUpdates)
            select notice.NormalizedName).Distinct().ToListAsync();
        if (wanted.Count == 0)
        {
            return [];
        }

        return await _dbSet.AsNoTracking()
            .Where(l => l.Outcome == PluginDownloadOutcome.Found && l.DownloadUrl != null && wanted.Contains(l.NormalizedName)
                && (l.ValidationState == PluginFileValidationState.NotChecked
                    || (l.ValidationState == PluginFileValidationState.Failed && l.ValidationNextAttemptUtc != null && l.ValidationNextAttemptUtc <= now)))
            .OrderBy(l => l.ValidationNextAttemptUtc != null)      // never checked first (false sorts before true)
            .ThenBy(l => l.ValidationNextAttemptUtc)
            .ThenBy(l => l.CheckedAtUtc)
            .Take(limit)
            .ToListAsync();
    }

    public async Task SaveValidationAsync(PluginDownloadLookup row)
    {
        var existing = await _dbSet.FirstOrDefaultAsync(l => l.Id == row.Id);
        if (existing is null)
        {
            return;      // the row went away (retention, a re-ask): nothing to record it on
        }

        existing.CopyValidationFrom(row);
        await _context.SaveChangesAsync();
    }

    public async Task SaveAsync(PluginDownloadLookup row)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await _dbSet.FirstOrDefaultAsync(l =>
                l.MarketplaceKey == row.MarketplaceKey && l.NormalizedName == row.NormalizedName && l.Version == row.Version);

            if (existing is null)
            {
                await _dbSet.AddAsync(new PluginDownloadLookup { Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id }.CopyFrom(row));
            }
            else
            {
                existing.CopyFrom(row);
            }

            try
            {
                await _context.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException) when (attempt == 0 && existing is null)
            {
                // Someone else's ask for the same plugin finished first: theirs is the row; ours replaces its answer.
                _context.ChangeTracker.Clear();
            }
        }
    }
}

internal static class PluginDownloadLookupCopy
{
    /// <summary>Copies everything but the identity, so an existing row keeps its own.</summary>
    public static PluginDownloadLookup CopyFrom(this PluginDownloadLookup to, PluginDownloadLookup from)
    {
        to.MarketplaceKey = from.MarketplaceKey;
        to.NormalizedName = from.NormalizedName;
        to.Version = from.Version;
        to.Outcome = from.Outcome;
        to.DownloadUrl = from.DownloadUrl;
        to.MatchedName = from.MatchedName;
        to.MatchedPageUrl = from.MatchedPageUrl;
        to.MatchedVersion = from.MatchedVersion;
        to.Reason = from.Reason;
        to.ResponseJson = from.ResponseJson;
        to.ResponseTruncated = from.ResponseTruncated;
        to.HttpStatus = from.HttpStatus;
        to.CheckedAtUtc = from.CheckedAtUtc;
        to.NextAttemptUtc = from.NextAttemptUtc;
        to.Attempts = from.Attempts;
        return to;
    }

    /// <summary>Copies only what was learned about the file.</summary>
    public static void CopyValidationFrom(this PluginDownloadLookup to, PluginDownloadLookup from)
    {
        to.ValidationState = from.ValidationState;
        to.ValidationReason = from.ValidationReason;
        to.FileKind = from.FileKind;
        to.FileSha256 = from.FileSha256;
        to.FileSizeBytes = from.FileSizeBytes;
        to.PreviousFileSha256 = from.PreviousFileSha256;
        to.FileHashChanges = from.FileHashChanges;
        to.PluginClassName = from.PluginClassName;
        to.PluginInfoName = from.PluginInfoName;
        to.PluginInfoAuthor = from.PluginInfoAuthor;
        to.PluginInfoVersion = from.PluginInfoVersion;
        to.ZipEntries = from.ZipEntries;
        to.ZipSourceFindings = from.ZipSourceFindings;
        to.ValidatedAtUtc = from.ValidatedAtUtc;
        to.ValidationNextAttemptUtc = from.ValidationNextAttemptUtc;
        to.ValidationAttempts = from.ValidationAttempts;
    }
}
