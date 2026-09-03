// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Seeds four starter <see cref="Plan"/> rows on a fresh deployment, matching RustArchon's own
/// original marketing tiers (Wood/Stone/Metal/HQM) as reasonable example data - a self-hoster is free
/// to rename, recolor, reprice, deactivate, or add to these; nothing about the schema restricts them
/// to this set (see <see cref="Data.Plan"/>'s remarks on why <c>Name</c> replaced a fixed enum).
/// </summary>
/// <remarks>
/// <strong>Idempotent</strong>, safe to call on every Api startup (mirrors
/// <see cref="PlatformSettingsRegistry"/>) - gated on "does any Plan exist at all", so an admin's later
/// edits (including deactivating, renaming via a new row, or superseding one of these) are never
/// overwritten by a later restart re-running this.
/// </remarks>
public static class PlanSeeder
{
    // Quarterly/Annual starting points, applied to each seeded Monthly price - see CreatePlanDto's
    // remarks and the Panel admin form's "recalculate from monthly" helper, which uses the same ratios
    // for plans an admin creates later.
    private const decimal QuarterlyMultiplier = 2.75m;
    private const decimal AnnualMultiplier = 10m;

    public static async Task EnsureDefaultsAsync(ApiDbContext dbContext, ILogger logger)
    {
        if (await dbContext.Set<Plan>().AnyAsync())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        dbContext.Set<Plan>().AddRange(
            BuildPlan("Wood", "#b08553", 0.00m, retentionDays: 30, hasRoles: false, maxServers: 1, maxUsers: 1, now),
            BuildPlan("Stone", "#9a988c", 5.00m, retentionDays: 60, hasRoles: false, maxServers: 1, maxUsers: 2, now),
            BuildPlan("Metal", "#7e94a6", 15.00m, retentionDays: 90, hasRoles: false, maxServers: 5, maxUsers: 10, now),
            BuildPlan("HQM", "#4fc3d9", 29.95m, retentionDays: 265, hasRoles: true, maxServers: 10, maxUsers: 20, now));

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Seeded the four starter Plan rows (Wood, Stone, Metal, HQM).");
    }

    private static Plan BuildPlan(
        string name, string colorCode, decimal monthlyPrice, int retentionDays, bool hasRoles,
        int maxServers, int maxUsers, DateTimeOffset now) => new()
    {
        Name = name,
        ColorCode = colorCode,
        MonthlyPrice = monthlyPrice,
        QuarterlyPrice = Math.Round(monthlyPrice * QuarterlyMultiplier, 2),
        AnnualPrice = Math.Round(monthlyPrice * AnnualMultiplier, 2),
        RetentionHistory = retentionDays,
        HasRoles = hasRoles,
        MaximumServers = maxServers,
        MaximumUsers = maxUsers,
        Active = true,
        CreatedById = Guid.Empty,
        CreatedOn = now
    };
}
