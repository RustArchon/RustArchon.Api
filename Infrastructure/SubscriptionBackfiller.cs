// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Opens a <see cref="Subscription"/> interval for any <see cref="Tenant"/> that doesn't currently have
/// an open one (see <see cref="Subscription"/>'s remarks - open, i.e. null
/// <see cref="Subscription.EndDate"/>, is what makes a row the tenant's current plan).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Controllers.AccountBootstrapController.EnsureTenant"/> only ever does this for a
/// <em>brand-new</em> Organization at the moment it's created - it was never retroactive, so every
/// Organization that existed before the Plan/Subscription system shipped (or before an admin ever set
/// <see cref="PlatformSettingsRegistry.DefaultPlanId"/>) was silently left planless. Confirmed live:
/// this exact gap, on a pre-existing dev tenant. <see cref="Subscription"/>'s own remarks document
/// "every Organization has an open row from the moment it's created" as an invariant this repo
/// otherwise relies on (e.g. plan-limit enforcement on <c>RustServersController.Create</c> fails open
/// when it can't find one) - this closes that gap for tenants the normal bootstrap path never touched.
/// </para>
/// <para>
/// <strong>Idempotent</strong>, safe to call on every Api startup (same pattern as
/// <see cref="PlatformSettingsRegistry"/>/<see cref="PlanSeeder"/>) - only ever touches a Tenant with
/// no open <see cref="Subscription"/> row, so a tenant an admin has already assigned (or upgraded) is
/// never revisited, and a tenant's existing history is only ever appended to, never rewritten.
/// </para>
/// <para>
/// One consequence worth knowing if a cancellation/lapse flow is ever added: a tenant whose rows are
/// <em>all</em> closed reads as planless here and would be re-opened onto the default plan on the next
/// restart. That's correct today, when the only way to have no open row is a bug or a tenant older
/// than this system, but "deliberately ended, stays ended" would need an explicit marker rather than
/// just the absence of an open row.
/// </para>
/// <para>
/// Run after both <see cref="PlatformSettingsRegistry.EnsureDefaultsAsync"/> (so
/// <see cref="PlatformSettingsRegistry.DefaultPlanId"/>'s row exists to read) and
/// <see cref="PlanSeeder.EnsureDefaultsAsync"/> (so there's at least one Plan to fall back to) - see
/// <c>Program.cs</c>'s startup ordering.
/// </para>
/// </remarks>
public static class SubscriptionBackfiller
{
    public static async Task EnsureAllTenantsHavePlanAsync(
        ApiDbContext dbContext, Billing.IInvoiceService invoiceService, ILogger logger)
    {
        // "No *open* row", not "no rows at all" - Subscription is history, so a tenant can have plenty of
        // closed rows and still have no current plan (see Subscription's remarks). Matching on EndDate is
        // what keeps this aligned with GetForTenantAsync, which is the thing whose null answer this
        // exists to prevent.
        var planlessTenantIds = await dbContext.Set<Tenant>()
            .Where(t => !dbContext.Set<Subscription>().Any(tp => tp.TenantId == t.Id && tp.EndDate == null))
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
            startingPlan = await dbContext.Set<Plan>()
                .Include(p => p.Prices)
                .FirstOrDefaultAsync(p => p.Id == defaultPlanId);
        }

        // Cheapest by per-month rate across whichever terms each plan is offered on - the same basis
        // IPlanRepository.GetCheapestActiveAsync uses, ordered in memory for the same reason: it's a min
        // over a child collection, not a column, and the active catalog is a handful of rows.
        if (startingPlan is null)
        {
            var activePlans = await dbContext.Set<Plan>()
                .Include(p => p.Prices)
                .Where(p => p.Active)
                .ToListAsync();

            startingPlan = activePlans
                .OrderBy(p => p.Prices.Count == 0 ? decimal.MaxValue : p.Prices.Min(pp => pp.MonthlyEquivalentFor(1)))
                .ThenBy(p => p.CreatedOn)
                .FirstOrDefault();
        }

        if (startingPlan is null)
        {
            // Same "shouldn't happen, but don't crash startup over it" posture as PlanSeeder failing
            // to run would leave this in - EnsureTenant throws in this situation because a live sign-up
            // has nowhere else to go, but a boot-time backfill failing shouldn't take the whole Api
            // down. The gap just persists until the next restart with a usable Plan actually present.
            logger.LogWarning(
                "Skipped Subscription backfill for {Count} tenant(s) - no active Plan exists to assign.",
                planlessTenantIds.Count);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var periodEnd = now.AddMonths(BillingTerms.Monthly);

        // The same capacity rule every other path uses. These tenants have servers already, but their
        // count is deliberately not fed in: entitlement is something bought, and granting extra slots
        // for free because a tenant happened to predate the subscription system would be inventing a
        // purchase nobody made. They get what the plan bundles, and the server guard tells them if
        // that isn't enough.
        var quantity = Billing.PlanChangeCalculator.ResolveQuantity(
            startingPlan, BillingTerms.Monthly, requested: null, serverCount: 0);
        var openingAmount = Billing.PlanChangeCalculator.PriceFor(startingPlan, BillingTerms.Monthly, quantity);

        var openedPeriods = new List<SubscriptionPeriod>(planlessTenantIds.Count);

        foreach (var tenantId in planlessTenantIds)
        {
            var subscription = new Subscription
            {
                TenantId = tenantId,
                PlanId = startingPlan.Id,
                StartDate = now
            };
            dbContext.Set<Subscription>().Add(subscription);

            // Billing anchored at now rather than at whenever the tenant was created: these are tenants
            // that predate the subscription system entirely, so there is no earlier period they can be
            // said to have already paid for. Starting the clock today is the only honest option, and
            // matches what a brand-new Organization gets (AccountBootstrapController.EnsureTenant).
            var period = new SubscriptionPeriod
            {
                Subscription = subscription,
                TermMonths = BillingTerms.Monthly,
                Quantity = quantity,
                PeriodStart = now,
                PeriodEnd = periodEnd,
                StartDate = now,
                EndDate = periodEnd,
                EarnedAmount = openingAmount
            };
            dbContext.Set<SubscriptionPeriod>().Add(period);
            openedPeriods.Add(period);
        }

        await dbContext.SaveChangesAsync();

        // Invoiced like any other period - service is paid in advance, and a tenant that predates the
        // subscription system is starting a real one today rather than being granted a free month. No
        // invoice is raised retroactively for the time before now: there was no subscription then, so
        // there is nothing to bill for it.
        foreach (var period in openedPeriods)
        {
            await invoiceService.IssueForPeriodAsync(
                period,
                period.EarnedAmount,
                $"{startingPlan.Name} - {BillingTerms.Describe(BillingTerms.Monthly)}, "
                + $"{now:d MMM yyyy} - {periodEnd:d MMM yyyy}");
        }

        logger.LogInformation(
            "Backfilled {Count} tenant(s) with no Subscription onto '{PlanName}'.",
            planlessTenantIds.Count, startingPlan.Name);
    }
}
