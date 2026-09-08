// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Moves subscriptions forward in time: rolls a billing period over when it ends, and applies any
/// <see cref="ScheduledPlanChange"/> that has come due.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a deferred change would sit in the table forever and a tenant's
/// <see cref="Subscription.PeriodEnd"/> would drift permanently into the past - which would then feed
/// wrong proration into every future change, since proration measures against the current period.
/// Deferred changes are the whole downgrade story, so this is load-bearing rather than housekeeping.
/// </para>
/// <para>
/// <strong>Idempotent and catch-up safe.</strong> Each pass advances every subscription whose period
/// has already ended, looping until it's back in the future - so a deployment that was down for two
/// months lands in the right place rather than one period along. Work is selected by date, never by
/// "what changed since last time", so a missed pass costs nothing but latency.
/// </para>
/// <para>
/// Runs in the Api rather than the Worker deliberately: it's a database-only concern with no RCON
/// involvement, and the Api is where the DbContext, migrations and the rest of the plan machinery
/// already live. It's also single-instance-safe only in the sense the rest of this deployment is - see
/// the migration remarks in <c>Program.cs</c> about scaling out.
/// </para>
/// </remarks>
public class SubscriptionScheduleService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SubscriptionScheduleService> logger) : BackgroundService
{
    /// <summary>
    /// Hourly. Nothing here is latency-sensitive - a renewal or a scheduled change landing up to an
    /// hour after midnight is invisible to a user, and the query is cheap enough that polling more
    /// often would only add load.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Guards the catch-up loop against spinning forever on a subscription with corrupt dates (a
    /// zero-or-negative-length period would never advance past "now"). Twenty years of monthly periods
    /// is far past any legitimate backlog.
    /// </summary>
    private const int MaxPeriodsPerPass = 240;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One pass immediately on startup, so a deployment that was down over a renewal date catches up
        // as soon as it's back rather than waiting out the first interval.
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad pass kill the loop - the next one retries from current state, and
                // everything it does is derived from dates rather than from what this pass managed.
                logger.LogError(ex, "Subscription schedule pass failed; will retry in {Interval}.", Interval);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// One sweep: every subscription whose period has ended renews, and every scheduled change that has
    /// come due lands.
    /// </summary>
    /// <remarks>
    /// Internal rather than private only so <see cref="DemoDataSeeder"/> can drive it directly against a
    /// <see cref="SimulatedClock"/>. Everything it does is derived from the injected clock and from
    /// current state, so calling it on a simulated instant produces exactly what the hourly timer would
    /// have produced had the system been running then - which is the whole reason the demo history is
    /// worth trusting. Nothing else calls it; the timer above remains the only production caller.
    /// </remarks>
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceService>();
        var compression = scope.ServiceProvider.GetRequiredService<Administration.IRoleCompressionService>();

        var now = timeProvider.GetUtcNow();

        // Tenants with a change that has come due. Selected separately and unioned in below rather than
        // relying on an ended period to surface them: a change's effective date and its subscription's
        // period end are usually the same instant, but nothing guarantees it, and a subscription whose
        // period was already rolled forward would otherwise never be looked at again while a due change
        // sat pending against it - which is exactly what happened before this existed.
        var tenantsWithDueChange = await dbContext.Set<ScheduledPlanChange>()
            .Where(spc => spc.AppliedOn == null && spc.CancelledOn == null && spc.EffectiveDate <= now)
            .Select(spc => spc.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Every tenant that needs attention: its current billing period has ended, or it has a change
        // waiting. Ordered for stable, reproducible logs.
        var tenantIds = await dbContext.Set<SubscriptionPeriod>()
            .Where(t => t.Subscription.EndDate == null
                && (t.PeriodEnd <= now || tenantsWithDueChange.Contains(t.Subscription.TenantId)))
            .Select(t => t.Subscription.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var tenantId in tenantIds.OrderBy(id => id))
        {
            await AdvanceAsync(dbContext, subscriptions, invoices, compression, tenantId, now, cancellationToken);
        }
    }

    /// <summary>
    /// Walks one subscription forward to the present, renewing each period that has ended and applying
    /// any scheduled change that has come due along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each step advances to whichever comes first: the end of the current period, or the effective
    /// date of a change that is already due. Renewals happen only while the period ends strictly
    /// <em>before</em> the next due change, so a subscription that sat unattended across several
    /// periods lands on the right plan on the right date rather than skipping the intervening ones.
    /// </para>
    /// <para>
    /// A change takes effect at its own <see cref="ScheduledPlanChange.EffectiveDate"/>, and is
    /// selected by "has it come due?" (<c>EffectiveDate &lt;= now</c>) rather than by matching a period
    /// boundary exactly. That distinction is deliberate and was found the hard way: an earlier version
    /// only considered a change whose <c>EffectiveDate</c> was <c>&lt;=</c> the current
    /// <c>PeriodEnd</c>, which silently skipped one whose date sat <em>two milliseconds</em> past the
    /// boundary - it renewed the old plan instead and wouldn't have reconsidered the change for another
    /// whole period. The two values are written from a single computed instant by
    /// <c>SubscriptionService.ApplyAsync</c> so they match exactly in practice, but a downgrade
    /// silently not happening for a month is far too quiet a failure to leave resting on that.
    /// </para>
    /// </remarks>
    private async Task AdvanceAsync(
        ApiDbContext dbContext,
        ISubscriptionRepository subscriptions,
        IInvoiceService invoices,
        Administration.IRoleCompressionService compression,
        Guid tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await subscriptions.GetCurrentTermAsync(tenantId, cancellationToken);
        if (current is null)
        {
            return;
        }

        var blockedChangeIds = new HashSet<Guid>();

        for (var guard = 0; guard < MaxPeriodsPerPass; guard++)
        {
            var change = await dbContext.Set<ScheduledPlanChange>()
                .Include(spc => spc.Plan).ThenInclude(p => p.Prices)
                .Where(spc => spc.TenantId == tenantId
                    && spc.AppliedOn == null
                    && spc.CancelledOn == null
                    && spc.EffectiveDate <= now
                    && !blockedChangeIds.Contains(spc.Id))
                .OrderBy(spc => spc.EffectiveDate)
                .FirstOrDefaultAsync(cancellationToken);

            // Renew first only while this period genuinely ends before the next due change - otherwise
            // an unattended subscription would roll straight past the date its change should have
            // landed on.
            if (current.PeriodEnd <= now && (change is null || current.PeriodEnd < change.EffectiveDate))
            {
                current = await RenewAsync(dbContext, invoices, current, cancellationToken);
                continue;
            }

            if (change is null)
            {
                break;
            }

            // Never start a period before the one it replaces - a change whose date somehow predates the
            // current period still lands at that period's own start.
            var effectiveAt = change.EffectiveDate < current.PeriodStart ? current.PeriodStart : change.EffectiveDate;

            var applied = await TryApplyChangeAsync(dbContext, invoices, compression, tenantId, current, change, effectiveAt, cancellationToken);
            if (applied is null)
            {
                // Can't apply it right now (see TryApplyChangeAsync). Skip it for the rest of this pass
                // so the loop doesn't spin on it, and let the period renew on the current plan instead;
                // it stays pending and is retried on the next pass.
                blockedChangeIds.Add(change.Id);
                continue;
            }

            current = applied;
        }

        if (current.PeriodEnd <= now)
        {
            logger.LogError(
                "Subscription for tenant {TenantId} still has a period ending {PeriodEnd} after {Max} advances - "
                + "check its period dates.",
                tenantId, current.PeriodEnd, MaxPeriodsPerPass);
        }
    }

    /// <summary>
    /// Turns a due <see cref="ScheduledPlanChange"/> into a new subscription interval. Returns the new
    /// interval, or <c>null</c> when the change can't be applied right now.
    /// </summary>
    /// <remarks>
    /// The one thing that can block it is the server-count rule: a downgrade scheduled months ago is
    /// still subject to it, and the tenant may well have added servers since. Applying it anyway would
    /// leave them over their limit, so the change stays pending and the current plan renews instead -
    /// they keep the plan they're actually using, and the change lands on a later pass once they're
    /// back under the target's limit (or they cancel it). Deliberately not silently cancelled: it's
    /// what the tenant asked for, and cancelling it on their behalf would be a decision this service
    /// has no business making.
    /// </remarks>
    private async Task<SubscriptionPeriod?> TryApplyChangeAsync(
        ApiDbContext dbContext,
        IInvoiceService invoices,
        Administration.IRoleCompressionService compression,
        Guid tenantId,
        SubscriptionPeriod current,
        ScheduledPlanChange change,
        DateTimeOffset effectiveAt,
        CancellationToken cancellationToken)
    {
        // No ambient tenant in a background pass, so the tenant is named explicitly. Soft-deleted
        // servers stay excluded without a manual condition - see JumpStart's AcrossAllTenants.
        var serverCount = await dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .CountAsync(s => s.TenantId == tenantId, cancellationToken);

        // Explicit null check rather than a lifted comparison - a plan with no ceiling can never block
        // a scheduled change, and relying on `null < n` being false to express that is too subtle to
        // leave to the reader (it's the same shape that inverted GetPlanOptionsAsync's guard).
        if (change.Plan.MaximumServers is { } ceiling && ceiling < serverCount)
        {
            logger.LogWarning(
                "Scheduled change to '{PlanName}' for tenant {TenantId} (due {EffectiveDate}) can't be applied - "
                + "the plan allows {Max} server(s) and the tenant has {Count}. Renewing the current plan instead; "
                + "will retry.",
                change.Plan.Name, tenantId, change.EffectiveDate, change.Plan.MaximumServers, serverCount);
            return null;
        }

        // The same re-check, one level down: a release of capacity accepted months ago is still subject
        // to the rule that slots can't fall below the servers using them, and servers can be added in
        // between. Blocked the same way as the ceiling case - kept pending, current plan renews - rather
        // than clamped upward, because quietly billing for more slots than the tenant agreed to buy is
        // the one outcome worse than the change being late.
        if (change.Quantity < serverCount)
        {
            logger.LogWarning(
                "Scheduled change for tenant {TenantId} (due {EffectiveDate}) can't be applied - it releases "
                + "capacity to {Quantity} slot(s) and the tenant is running {Count} server(s). Renewing the "
                + "current plan instead; will retry.",
                tenantId, change.EffectiveDate, change.Quantity, serverCount);
            return null;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var openPlan = current.Subscription;
        var owningPlanId = openPlan.Id;

        // A scheduled change lands on a period boundary, so unlike a mid-period change there is nothing
        // to re-price - the outgoing slice already ran its full course.
        if (openPlan.PlanId != change.PlanId)
        {
            openPlan.EndDate = effectiveAt;
            await dbContext.SaveChangesAsync(cancellationToken);

            var openedPlan = new Subscription
            {
                TenantId = tenantId,
                PlanId = change.PlanId,
                StartDate = effectiveAt
            };
            dbContext.Set<Subscription>().Add(openedPlan);
            await dbContext.SaveChangesAsync(cancellationToken);

            owningPlanId = openedPlan.Id;
        }

        // A fresh period, priced at list - nothing has been charged against it yet.
        var periodEnd = effectiveAt.AddMonths((int)change.TermMonths);
        var opened = new SubscriptionPeriod
        {
            SubscriptionId = owningPlanId,
            TermMonths = change.TermMonths,
            Quantity = change.Quantity,
            PeriodStart = effectiveAt,
            PeriodEnd = periodEnd,
            StartDate = effectiveAt,
            EndDate = periodEnd,
            EarnedAmount = PlanChangeCalculator.PriceFor(change.Plan, change.TermMonths, change.Quantity)
        };
        dbContext.Set<SubscriptionPeriod>().Add(opened);

        change.AppliedOn = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        // A new period, so a new invoice for its full price - inside the same transaction, so a failure
        // here rolls back the change rather than leaving a period nobody will ever be billed for.
        await invoices.IssueForPeriodAsync(
            opened,
            opened.EarnedAmount,
            PeriodDescription(change.Plan.Name, opened),
            cancellationToken);

        // A move to a plan without role separation collapses the Organization onto the single
        // built-in Owner role - every member holds it, and the Organization's own roles are retired.
        // The customer agreed to this when they accepted the downgrade, which was typically weeks
        // ago; the set of people promoted is re-derived here rather than taken from anything stored
        // then, because the membership will have moved on. See IRoleCompressionService.
        //
        // Inside the transaction: a compression that succeeded while the plan change rolled back
        // would strip an Organization's roles and leave it on the plan that entitled it to them.
        if (!change.Plan.HasRoles)
        {
            var compressed = await compression.CompressAsync(
                tenantId,
                $"Downgraded to '{change.Plan.Name}', which does not include role separation.",
                cancellationToken);

            if (compressed.IsNeeded)
            {
                logger.LogWarning(
                    "Tenant {TenantId} moved to '{PlanName}' and was compressed: {Promoted} member(s) "
                    + "now hold Owner, {Removed} custom role(s) retired.",
                    tenantId, change.Plan.Name, compressed.MembersPromoted, compressed.RolesRemoved);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Applied scheduled change for tenant {TenantId}: now on '{PlanName}' billed {Term}, period {Start} to {End}.",
            tenantId, change.Plan.Name, change.TermMonths, effectiveAt, periodEnd);

        // Reload so the returned slice carries its plan navigation for the next loop iteration.
        return await dbContext.Set<SubscriptionPeriod>()
            .Include(t => t.Subscription).ThenInclude(tp => tp.Plan)
            .FirstAsync(t => t.Id == opened.Id, cancellationToken);
    }

    /// <summary>
    /// Rolls one billing period forward on the same plan and term by <strong>inserting</strong> a new
    /// slice - never by editing the one that just ended.
    /// </summary>
    /// <remarks>
    /// This is the whole reason billing periods were split out of <see cref="Subscription"/>. The earlier
    /// design advanced the period in place, so a tenant two years into an unchanged monthly plan had a
    /// single row describing only the current month and no trace of the twenty-three before it - "show
    /// me my billing history" had nothing to read. Each period is now its own row, and the plan-history
    /// row it hangs off is untouched by renewal.
    /// </remarks>
    private async Task<SubscriptionPeriod> RenewAsync(
        ApiDbContext dbContext,
        IInvoiceService invoices,
        SubscriptionPeriod current,
        CancellationToken cancellationToken)
    {
        var plan = current.Subscription;
        var periodStart = current.PeriodEnd;
        var periodEnd = periodStart.AddMonths((int)current.TermMonths);

        var renewed = new SubscriptionPeriod
        {
            SubscriptionId = plan.Id,
            TermMonths = current.TermMonths,
            // Capacity carries across a renewal untouched - nothing about rolling a period forward
            // changes what the tenant bought, and pricing the new period without it would silently bill
            // a per-server subscription as if it held a single slot.
            Quantity = current.Quantity,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            StartDate = periodStart,
            EndDate = periodEnd,
            EarnedAmount = PlanChangeCalculator.PriceFor(plan.Plan, current.TermMonths, current.Quantity)
        };

        dbContext.Set<SubscriptionPeriod>().Add(renewed);
        await dbContext.SaveChangesAsync(cancellationToken);

        // The renewal invoice. Not wrapped in its own transaction: if this fails, the period still
        // exists and the next pass finds it unbilled and issues then - which is the same self-healing
        // property that makes the whole service safe to re-run. A period silently never billed is the
        // outcome worth preventing, and the "already billed?" check is what prevents the opposite.
        await invoices.IssueForPeriodAsync(
            renewed,
            renewed.EarnedAmount,
            PeriodDescription(plan.Plan.Name, renewed),
            cancellationToken);

        logger.LogInformation(
            "Renewed subscription for tenant {TenantId} on '{PlanName}' ({Term}); period now {Start} to {End}.",
            plan.TenantId, plan.Plan.Name, current.TermMonths, periodStart, periodEnd);

        renewed.Subscription = plan;
        return renewed;
    }

    /// <summary>How a whole billing period reads as an invoice line.</summary>
    private static string PeriodDescription(string planName, SubscriptionPeriod period)
    {
        var span = $"{period.PeriodStart:d MMM yyyy} - {period.PeriodEnd:d MMM yyyy}";
        var term = BillingTerms.Describe(period.TermMonths);

        // Only says "slots" when there is more than one, so a flat-tier invoice doesn't carry a
        // quantity the tenant has no control over and no reason to think about.
        return period.Quantity > 1
            ? $"{planName} - {term}, {period.Quantity} server slots, {span}"
            : $"{planName} - {term}, {span}";
    }
}
