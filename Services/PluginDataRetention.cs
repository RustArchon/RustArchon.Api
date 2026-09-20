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

namespace RustArchon.Api.Services;

/// <summary>Removes recorded plugin data that is older than the organization's plan keeps it.</summary>
public interface IPluginDataRetention
{
    /// <summary>Deletes what is past retention as of <paramref name="now"/>. Returns how many chunks (combat and position) were removed.</summary>
    Task<int> PruneAsync(DateTimeOffset now);
}

/// <inheritdoc cref="IPluginDataRetention" />
/// <remarks>
/// <para>
/// Each organization keeps its plugin-recorded data for its current plan's <see cref="Plan.RetentionHistory"/> days.
/// An organization with no current subscription, or a plan whose retention is not a positive number, is held to
/// <see cref="DefaultRetentionDays"/> - the shortest the catalog offers - so a missing or odd value can never mean
/// "keep forever" (storage nobody is paying for) and never means "delete everything at once" either.
/// </para>
/// <para>
/// A chunk is judged by the time of its <b>newest</b> event, so a chunk that still holds anything inside the window is
/// kept whole. Deletion is permanent by design: this is the enforcement of the retention the plan advertises, and
/// nothing else in the Api enforced it before this.
/// </para>
/// </remarks>
public class PluginDataRetention(ApiDbContext context, ILogger<PluginDataRetention> logger) : IPluginDataRetention
{
    public const int DefaultRetentionDays = 30;

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
            .ToList();

        var removed = 0;
        foreach (var tenant in tenants)
        {
            var days = retention.TryGetValue(tenant, out var planDays) && planDays > 0 ? planDays : DefaultRetentionDays;
            var cutoff = now.AddDays(-days);

            var count = await context.PluginCombatChunks.AcrossAllTenants()
                .Where(c => c.TenantId == tenant && c.ToUtc < cutoff)
                .ExecuteDeleteAsync();

            if (count > 0)
            {
                logger.LogInformation("Pruned {Count} combat chunks older than {Days} days for organization {TenantId}.", count, days, tenant);
                removed += count;
            }

            var positionCount = await context.PluginPositionChunks.AcrossAllTenants()
                .Where(c => c.TenantId == tenant && c.ToUtc < cutoff)
                .ExecuteDeleteAsync();

            if (positionCount > 0)
            {
                logger.LogInformation("Pruned {Count} position chunks older than {Days} days for organization {TenantId}.", positionCount, days, tenant);
                removed += positionCount;
            }
        }

        return removed;
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
