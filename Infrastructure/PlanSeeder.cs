// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

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
            // Wood is the only one marked one-per-owner: it costs nothing, so without that an
            // account could mint free server slots by creating Organizations. The paid tiers are
            // deliberately unlimited - a second Organization on one of those is a second
            // subscription, which is a customer rather than a problem.
            BuildPlan("Wood", "#b08553", 0.00m, retentionDays: 30, hasRoles: false, maxServers: 1, maxUsers: 1, now, onePerOwner: true),
            BuildPlan("Stone", "#9a988c", 5.00m, retentionDays: 60, hasRoles: false, maxServers: 1, maxUsers: 2, now),
            BuildPlan("Metal", "#7e94a6", 15.00m, retentionDays: 90, hasRoles: false, maxServers: 5, maxUsers: 10, now),
            BuildPlan("HQM", "#4fc3d9", 29.95m, retentionDays: 265, hasRoles: true, maxServers: 10, maxUsers: 20, now));

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Seeded the four starter Plan rows (Wood, Stone, Metal, HQM).");
    }

    /// <summary>
    /// Builds a flat-tier plan offered on all three of RustArchon's own terms.
    /// </summary>
    /// <remarks>
    /// Flat is only the shape of the <em>seeded example</em> data, not a limit of the model: every price
    /// row here sets <c>UnitAmount</c> to zero and <c>IncludedUnits</c> to the plan's ceiling, which is
    /// what makes capacity capped rather than bought. A per-server plan is the same rows with a non-zero
    /// unit amount and no ceiling - see <see cref="Data.PlanPrice"/>.
    /// </remarks>
    private static Plan BuildPlan(
        string name, string colorCode, decimal monthlyPrice, int retentionDays, bool hasRoles,
        int maxServers, int maxUsers, DateTimeOffset now, bool onePerOwner = false) => new()
    {
        Name = name,
        ColorCode = colorCode,
        PricingModel = PricingModel.Flat,
        RetentionHistory = retentionDays,
        HasRoles = hasRoles,
        OnePerOwner = onePerOwner,
        MaximumServers = maxServers,
        MaximumUsers = maxUsers,
        Active = true,
        CreatedById = Guid.Empty,
        CreatedOn = now,
        Prices =
        [
            FlatPrice(BillingTerms.Monthly, monthlyPrice, maxServers),
            FlatPrice(BillingTerms.Quarterly, Math.Round(monthlyPrice * QuarterlyMultiplier, 2), maxServers),
            FlatPrice(BillingTerms.Annual, Math.Round(monthlyPrice * AnnualMultiplier, 2), maxServers)
        ]
    };

    private static PlanPrice FlatPrice(int termMonths, decimal amount, int includedUnits) => new()
    {
        TermMonths = termMonths,
        BaseAmount = amount,
        IncludedUnits = includedUnits,
        UnitAmount = 0m,
        Currency = "USD"
    };
}
