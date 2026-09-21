// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.ObjectStorage;

namespace RustArchon.Api.Services;

/// <summary>Removes recorded data that is older than the organization's plan keeps it.</summary>
public interface IPluginDataRetention
{
    /// <summary>
    /// Deletes what is past retention as of <paramref name="now"/>: plugin chunks, console/chat and kill-feed events, stats snapshots,
    /// plugin update notices not heard again, and the pictures and rows of past wipes' maps. Returns how many database rows were removed.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset now);
}

/// <inheritdoc cref="IPluginDataRetention" />
/// <remarks>
/// <para>
/// Each organization keeps its recorded data for its current plan's <see cref="Plan.RetentionHistory"/> days ("console/chat/player
/// history"). An organization with no current subscription, or a plan whose retention is not a positive number, is held to
/// <see cref="DefaultRetentionDays"/> - the shortest the catalog offers - so a missing or odd value can never mean
/// "keep forever" (storage nobody is paying for) and never means "delete everything at once" either.
/// </para>
/// <para>
/// A chunk is judged by the time of its <b>newest</b> event, so a chunk that still holds anything inside the window is
/// kept whole. Deletion is permanent by design: this is the enforcement of the retention the plan advertises.
/// </para>
/// <para>
/// A server's maps are judged differently: a wipe's map is the one thing a server always has, so the newest map row of each server
/// is <b>never</b> removed, however old; earlier wipes' maps go once neither their last sighting nor their upload is inside the window.
/// Their pictures (the original and the display copy) are deleted from storage first, and the row only when that worked, so a storage
/// failure is retried on the next pass instead of leaving a picture nobody can find.
/// </para>
/// <para>
/// Player sessions are deliberately not pruned here: they carry the geolocation, VPN and Steam ban lookups and the names the Panel
/// shows for players, which are worth more than the storage they take. Decision (Scott, 2026-09-20): they are kept for as long as the server
/// is; that history is the point of the service and is disclosed in the privacy policy, so the plan's "player history" days do not apply to them. Deleting rows is done in batches, so the first pass over a table
/// that has never been pruned does not hold one enormous transaction.
/// </para>
/// </remarks>
public class PluginDataRetention(ApiDbContext context, IObjectStorage storage, ILogger<PluginDataRetention> logger) : IPluginDataRetention
{
    public const int DefaultRetentionDays = 30;

    /// <summary>How many rows one DELETE removes. Big enough to be quick, small enough to keep the transaction and lock short.</summary>
    public const int DeleteBatchSize = 5000;

    /// <summary>
    /// How long a spent or expired single-use token is kept before it is deleted: long enough to look into "the upload failed",
    /// short enough not to pile up.
    /// </summary>
    public static readonly TimeSpan TokenGrace = TimeSpan.FromDays(1);

    public async Task<int> PruneAsync(DateTimeOffset now)
    {
        // A tenant's plan, by its one open subscription. Not tenant-filtered: this is a platform job across everyone.
        var plans = await context.Set<Subscription>().IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.EndDate == null)
            .Select(s => new { s.TenantId, Days = s.Plan.RetentionHistory })
            .ToListAsync();
        var retention = plans.GroupBy(p => p.TenantId).ToDictionary(g => g.Key, g => g.Max(p => p.Days));

        var tenants = (await context.PluginCombatChunks.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .Union(await context.PluginPositionChunks.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .Union(await context.RconEvents.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .Union(await context.PlayerKillEvents.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .Union(await context.ServerInfoSnapshots.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .Union(await context.PluginMaps.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .Union(await context.PluginUpdateNotices.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .Union(await context.PluginUpdateAttempts.AcrossAllTenants().Select(c => c.TenantId).Distinct().ToListAsync())
            .ToList();

        var removed = 0;
        foreach (var tenant in tenants)
        {
            var days = retention.TryGetValue(tenant, out var planDays) && planDays > 0 ? planDays : DefaultRetentionDays;
            var cutoff = now.AddDays(-days);

            removed += await DeleteOldAsync(
                context.PluginCombatChunks.AcrossAllTenants().Where(c => c.TenantId == tenant && c.ToUtc < cutoff),
                "combat chunks", days, tenant);
            removed += await DeleteOldAsync(
                context.PluginPositionChunks.AcrossAllTenants().Where(c => c.TenantId == tenant && c.ToUtc < cutoff),
                "position chunks", days, tenant);
            removed += await DeleteOldAsync(
                context.RconEvents.AcrossAllTenants().Where(e => e.TenantId == tenant && e.CapturedAtUtc < cutoff),
                "console and chat events", days, tenant);
            removed += await DeleteOldAsync(
                context.PlayerKillEvents.AcrossAllTenants().Where(e => e.TenantId == tenant && e.OccurredAtUtc < cutoff),
                "kill feed events", days, tenant);
            removed += await DeleteOldAsync(
                context.ServerInfoSnapshots.AcrossAllTenants().Where(e => e.TenantId == tenant && e.CapturedAtUtc < cutoff),
                "server stats snapshots", days, tenant);
            removed += await DeleteOldAsync(
                context.PluginUpdateNotices.AcrossAllTenants().Where(e => e.TenantId == tenant && e.ReportedAtUtc < cutoff),
                "plugin update notices", days, tenant);
            removed += await DeleteOldAsync(
                context.PluginUpdateAttempts.AcrossAllTenants().Where(e => e.TenantId == tenant && e.State == PluginUpdateAttemptStates.Succeeded && e.StartedAtUtc < cutoff),
                "succeeded plugin update attempts", days, tenant);
            removed += await PruneMapsAsync(tenant, cutoff, days);
        }

        removed += await PruneSpentTokensAsync(now - TokenGrace);
        return removed;
    }

    private async Task<int> DeleteOldAsync<T>(IQueryable<T> old, string what, int days, Guid tenant) where T : class
    {
        var total = 0;
        int batch;
        do
        {
            batch = await old.Take(DeleteBatchSize).ExecuteDeleteAsync();
            total += batch;
        }
        while (batch == DeleteBatchSize);

        if (total > 0)
        {
            logger.LogInformation("Pruned {Count} {What} older than {Days} days for organization {TenantId}.", total, what, days, tenant);
        }

        return total;
    }

    /// <summary>Removes the maps of past wipes that are outside the window. Returns how many map rows went.</summary>
    private async Task<int> PruneMapsAsync(Guid tenant, DateTimeOffset cutoff, int days)
    {
        var rows = await context.PluginMaps.AcrossAllTenants().AsNoTracking()
            .Where(m => m.TenantId == tenant)
            .Select(m => new MapRow(m.Id, m.RustServerId, m.LastSeenUtc, m.UploadedAtUtc, m.ObjectKey, m.PreviewObjectKey))
            .ToListAsync();

        var removed = 0;
        foreach (var server in rows.GroupBy(r => r.RustServerId))
        {
            // The newest map of a server is the current world: never removed, whatever its age.
            var current = server.OrderByDescending(r => r.LastSeenUtc).ThenByDescending(r => r.UploadedAtUtc).ThenBy(r => r.Id).First();
            foreach (var old in server.Where(r => r.Id != current.Id && r.Newest < cutoff))
            {
                if (!await TryDeleteMapPicturesAsync(old))
                {
                    continue;     // left in place; the next pass tries again
                }

                await context.PluginMapUploadTokens.AcrossAllTenants().Where(t => t.PluginMapId == old.Id).ExecuteDeleteAsync();
                removed += await context.PluginMaps.AcrossAllTenants().Where(m => m.Id == old.Id).ExecuteDeleteAsync();
                logger.LogInformation("Pruned a map from a past wipe (server {ServerId}) older than {Days} days for organization {TenantId}.", old.RustServerId, days, tenant);
            }
        }

        return removed;
    }

    private async Task<bool> TryDeleteMapPicturesAsync(MapRow map)
    {
        try
        {
            foreach (var key in new[] { map.ObjectKey, map.PreviewObjectKey }.Where(k => !string.IsNullOrEmpty(k)).Distinct())
            {
                await storage.DeleteAsync(key!);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the pictures of an old map for server {ServerId}; it is kept and will be tried again.", map.RustServerId);
            return false;
        }
    }

    /// <summary>Removes upload and update tokens that expired more than <see cref="TokenGrace"/> ago. They are useless and only pile up.</summary>
    private async Task<int> PruneSpentTokensAsync(DateTimeOffset expiredBefore)
    {
        var removed = await context.PluginMapUploadTokens.AcrossAllTenants().Where(t => t.ExpiresAtUtc < expiredBefore).ExecuteDeleteAsync();
        removed += await context.PluginUpdateTokens.AcrossAllTenants().Where(t => t.ExpiresAtUtc < expiredBefore).ExecuteDeleteAsync();
        if (removed > 0)
        {
            logger.LogInformation("Pruned {Count} expired plugin tokens.", removed);
        }

        return removed;
    }

    private sealed record MapRow(Guid Id, Guid RustServerId, DateTimeOffset LastSeenUtc, DateTimeOffset? UploadedAtUtc, string? ObjectKey, string? PreviewObjectKey)
    {
        /// <summary>The latest sign of life the map has: last reported by the plugin, or last uploaded.</summary>
        public DateTimeOffset Newest => UploadedAtUtc is { } up && up > LastSeenUtc ? up : LastSeenUtc;
    }
}

/// <summary>Runs <see cref="IPluginDataRetention"/> every few hours. A failed pass is logged and tried again next time.</summary>
public class PluginDataPruneService(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<PluginDataPruneService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IPluginDataRetention>().PruneAsync(clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Plugin data retention pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
