// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Assigns a <see cref="Plan"/> to any <see cref="Tenant"/> that doesn't already have a
/// <see cref="TenantPlan"/> row.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Controllers.AccountBootstrapController.EnsureTenant"/> only ever does this for a
/// <em>brand-new</em> Organization at the moment it's created - it was never retroactive, so every
/// Organization that existed before the Plan/TenantPlan system shipped (or before an admin ever set
/// <see cref="PlatformSettingsRegistry.DefaultPlanId"/>) was silently left planless. Confirmed live:
/// this exact gap, on a pre-existing dev tenant. <see cref="TenantPlan"/>'s own remarks document
/// "every Organization must have exactly one from the moment it's created" as an invariant this repo
/// otherwise relies on (e.g. plan-limit enforcement on <c>RustServersController.Create</c> fails open
/// when it can't find one) - this closes that gap for tenants the normal bootstrap path never touched.
/// </para>
/// <para>
/// <strong>Idempotent</strong>, safe to call on every Api startup (same pattern as
/// <see cref="PlatformSettingsRegistry"/>/<see cref="PlanSeeder"/>) - only ever touches a Tenant that
/// still has zero <see cref="TenantPlan"/> rows, so a tenant an admin has already assigned (or
/// upgraded) is never revisited.
/// </para>
/// <para>
/// Run after both <see cref="PlatformSettingsRegistry.EnsureDefaultsAsync"/> (so
/// <see cref="PlatformSettingsRegistry.DefaultPlanId"/>'s row exists to read) and
/// <see cref="PlanSeeder.EnsureDefaultsAsync"/> (so there's at least one Plan to fall back to) - see
/// <c>Program.cs</c>'s startup ordering.
/// </para>
/// </remarks>
public static class TenantPlanBackfiller
{
    public static async Task EnsureAllTenantsHavePlanAsync(ApiDbContext dbContext, ILogger logger)
    {
        var planlessTenantIds = await dbContext.Set<Tenant>()
            .Where(t => !dbContext.Set<TenantPlan>().Any(tp => tp.TenantId == t.Id))
            .Select(t => t.Id)
            .ToListAsync();

        if (planlessTenantIds.Count == 0)
        {
            return;
        }

        // Same resolution AccountBootstrapController.EnsureTenant uses for a brand-new Organization -
        // the site admin's explicit choice if they've made one (honored even if that Plan has since
        // been deactivated, same reasoning as EnsureTenant), otherwise the cheapest currently-active
        // Plan. Resolved once, outside the loop below - every planless tenant backfilled in this pass
        // starts on the same plan, same as if they'd all just signed up under today's settings.
        var defaultPlanIdRaw = await dbContext.Set<PlatformSetting>()
            .Where(s => s.Key == PlatformSettingsRegistry.DefaultPlanId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        Plan? startingPlan = null;
        if (!string.IsNullOrWhiteSpace(defaultPlanIdRaw) && Guid.TryParse(defaultPlanIdRaw, out var defaultPlanId))
        {
            startingPlan = await dbContext.Set<Plan>().FirstOrDefaultAsync(p => p.Id == defaultPlanId);
        }

        startingPlan ??= await dbContext.Set<Plan>()
            .Where(p => p.Active)
            .OrderBy(p => p.MonthlyPrice)
            .FirstOrDefaultAsync();

        if (startingPlan is null)
        {
            // Same "shouldn't happen, but don't crash startup over it" posture as PlanSeeder failing
            // to run would leave this in - EnsureTenant throws in this situation because a live sign-up
            // has nowhere else to go, but a boot-time backfill failing shouldn't take the whole Api
            // down. The gap just persists until the next restart with a usable Plan actually present.
            logger.LogWarning(
                "Skipped TenantPlan backfill for {Count} tenant(s) - no active Plan exists to assign.",
                planlessTenantIds.Count);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        dbContext.Set<TenantPlan>().AddRange(planlessTenantIds.Select(tenantId => new TenantPlan
        {
            TenantId = tenantId,
            PlanId = startingPlan.Id,
            AssignedAtUtc = now
        }));

        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Backfilled {Count} tenant(s) with no TenantPlan onto '{PlanName}'.",
            planlessTenantIds.Count, startingPlan.Name);
    }
}
