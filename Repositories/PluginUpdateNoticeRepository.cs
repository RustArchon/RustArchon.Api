// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Repositories;

/// <summary>What storing a report changed: notices that are new, and notices whose newest version moved.</summary>
public sealed record PluginUpdateNoticeChanges(IReadOnlyList<PluginUpdateNotice> Added, IReadOnlyList<PluginUpdateNotice> Advanced);

public interface IPluginUpdateNoticeRepository : IRepository<PluginUpdateNotice>
{
    /// <summary>
    /// Merges a report from the Worker into what is held, by plugin name. A notice for a version already held only refreshes its times;
    /// one for a different version replaces it. A report older than what is held is ignored. Nothing is ever removed here: a plugin the
    /// game server has stopped mentioning (it reloaded, or UpdateChecker has not scanned yet) is not thereby up to date.
    /// </summary>
    Task<PluginUpdateNoticeChanges> MergeAsync(Guid tenantId, Guid rustServerId, IReadOnlyList<PluginUpdateNoticeInfo> updates, DateTimeOffset now);

    /// <summary>What is held for a server, by name. Tenant-filtered.</summary>
    Task<List<PluginUpdateNotice>> GetForServerAsync(Guid rustServerId);
}

public class PluginUpdateNoticeRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginUpdateNotice>(context, userContext), IPluginUpdateNoticeRepository
{
    /// <summary>
    /// The matching key: lower case letters and digits only, no longer than its column. UpdateChecker names a plugin by its class
    /// ("BlueprintShare", "MonumentAddons") while the server's plugin list gives its title ("Blueprint Share", "Monument Addons"), so
    /// spaces and punctuation must not matter. A name with no letters or digits at all is kept as it is (trimmed, lower case) so it still
    /// has a key.
    /// </summary>
    public static string Normalize(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (key.Length == 0)
        {
            key = name.Trim().ToLowerInvariant();
        }

        return key.Length <= 100 ? key : key[..100];
    }

    public async Task<PluginUpdateNoticeChanges> MergeAsync(
        Guid tenantId, Guid rustServerId, IReadOnlyList<PluginUpdateNoticeInfo> updates, DateTimeOffset now)
    {
        // A name given twice in one report: the one heard last wins.
        var incoming = updates
            .Where(u => !string.IsNullOrWhiteSpace(u.Name))
            .GroupBy(u => Normalize(u.Name))
            .ToDictionary(g => g.Key, g => g.OrderBy(u => u.LastSeenUtc).Last());

        // Two attempts: the second covers two reports for a brand-new plugin landing together, where the loser of the unique index
        // simply finds the winner's row.
        for (var attempt = 0; ; attempt++)
        {
            var existing = (await _dbSet.AcrossAllTenants().Where(n => n.RustServerId == rustServerId).ToListAsync())
                .ToDictionary(n => n.NormalizedName);

            var added = new List<PluginUpdateNotice>();
            var advanced = new List<PluginUpdateNotice>();

            foreach (var (key, info) in incoming)
            {
                if (!existing.TryGetValue(key, out var row))
                {
                    row = new PluginUpdateNotice { TenantId = tenantId, RustServerId = rustServerId, NormalizedName = key };
                    Apply(row, info, now);
                    row.FirstSeenUtc = info.FirstSeenUtc;
                    await _dbSet.AddAsync(row);
                    added.Add(row);
                    continue;
                }

                if (row.TenantId != tenantId)
                {
                    // A row for this server under another organization would mean the message lies about who owns the server.
                    throw new InvalidOperationException("An update notice for this server exists under a different organization.");
                }

                if (info.LastSeenUtc < row.LastSeenUtc)
                {
                    continue;   // older than what is held
                }

                if (string.Equals(row.LatestVersion, Truncate(info.LatestVersion, 50), StringComparison.Ordinal))
                {
                    var firstSeen = info.FirstSeenUtc < row.FirstSeenUtc ? info.FirstSeenUtc : row.FirstSeenUtc;
                    var timesSeen = Math.Max(row.TimesSeen, info.TimesSeen);
                    Apply(row, info, now);
                    row.FirstSeenUtc = firstSeen;
                    row.TimesSeen = timesSeen;
                }
                else
                {
                    Apply(row, info, now);
                    row.FirstSeenUtc = info.FirstSeenUtc;
                    advanced.Add(row);
                }
            }

            try
            {
                await _context.SaveChangesAsync();
                return new PluginUpdateNoticeChanges(added, advanced);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    public async Task<List<PluginUpdateNotice>> GetForServerAsync(Guid rustServerId) =>
        await _dbSet.AsNoTracking().Where(n => n.RustServerId == rustServerId).OrderBy(n => n.NormalizedName).ToListAsync();

    // The limits mirror the plugin's, so a well-behaved report is stored whole; anything longer is cut, not refused.
    private static void Apply(PluginUpdateNotice row, PluginUpdateNoticeInfo info, DateTimeOffset now)
    {
        row.Name = Truncate(info.Name.Trim(), 100);
        row.CurrentVersion = Truncate(info.CurrentVersion, 50);
        row.LatestVersion = Truncate(info.LatestVersion, 50);
        row.Url = Truncate(info.Url, 500);
        row.Marketplace = Truncate(info.Marketplace, 50);
        row.LastSeenUtc = info.LastSeenUtc;
        row.TimesSeen = info.TimesSeen;
        row.ReportedAtUtc = now;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
