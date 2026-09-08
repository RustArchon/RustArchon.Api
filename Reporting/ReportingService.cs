// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Reporting;

/// <inheritdoc cref="IReportingService" />
public class ReportingService(ApiDbContext dbContext, TimeProvider timeProvider) : IReportingService
{
    /// <summary>
    /// Ceiling on rows any one report returns.
    /// </summary>
    /// <remarks>
    /// Reports load their whole result set - see <see cref="ReportResult{TRow}"/> for why. This is the
    /// point at which that stops being reasonable, and hitting it sets
    /// <see cref="ReportResultBase.Truncated"/> so the reader is told rather than quietly shown a
    /// partial answer. It is not a page size: nothing pages past it.
    /// </remarks>
    public const int MaxRows = 5000;

    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-US");

    // ---------------------------------------------------------------------------------------------
    // Upcoming renewals
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ReportResult<UpcomingRenewalRowDto>> GetUpcomingRenewalsAsync(
        DateOnly from, DateOnly to, Guid? planId = null, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var (fromInstant, toExclusive) = Window(from, to);

        // The slice that will actually renew is the last one of its period - the only one whose EndDate
        // reaches PeriodEnd. Every earlier slice of the same period was closed off by a mid-period
        // change and ends before it. That identifies one row per open subscription without a GroupBy,
        // and both sides of the comparison are columns the database can use directly.
        var slices = await dbContext.Set<SubscriptionPeriod>()
            .Include(t => t.Subscription).ThenInclude(tp => tp.Tenant)
            .Include(t => t.Subscription).ThenInclude(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(t => t.Subscription.EndDate == null
                        && t.Subscription.Tenant.DeletedOn == null
                        && t.EndDate == t.PeriodEnd
                        && t.PeriodEnd >= fromInstant
                        && t.PeriodEnd < toExclusive
                        && (planId == null || t.Subscription.PlanId == planId))
            .OrderBy(t => t.PeriodEnd)
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);

        var truncated = Trim(slices);
        var tenantIds = slices.Select(s => s.Subscription.TenantId).Distinct().ToList();

        var serverCounts = await ServerCountsAsync(tenantIds, cancellationToken);
        var pendingChanges = await PendingChangesAsync(tenantIds, cancellationToken);

        var rows = new List<UpcomingRenewalRowDto>(slices.Count);
        foreach (var slice in slices)
        {
            var tenantId = slice.Subscription.TenantId;
            var serverCount = serverCounts.GetValueOrDefault(tenantId);

            // A pending change only counts toward this renewal if it lands on or before it. One dated
            // further out belongs to a later period and must not reprice this one.
            pendingChanges.TryGetValue(tenantId, out var pending);
            var appliesAtRenewal = pending is not null && pending.EffectiveDate <= slice.PeriodEnd;

            var renewalPlan = appliesAtRenewal ? pending!.Plan : slice.Subscription.Plan;
            var renewalTerm = appliesAtRenewal ? pending!.TermMonths : slice.TermMonths;
            // Priced against bought capacity, not against servers running. On a per-unit plan those
            // differ - a tenant can hold four slots and be using two - and the renewal charges for what
            // they bought.
            var renewalQuantity = appliesAtRenewal ? pending!.Quantity : slice.Quantity;
            var price = renewalPlan.Prices.FirstOrDefault(p => p.TermMonths == renewalTerm);

            rows.Add(new UpcomingRenewalRowDto
            {
                TenantId = tenantId,
                OrganizationName = slice.Subscription.Tenant.Name,
                ContactEmail = slice.Subscription.Tenant.ContactEmail,
                PlanName = slice.Subscription.Plan.Name,
                PlanColorCode = slice.Subscription.Plan.ColorCode,
                TermMonths = slice.TermMonths,
                RenewsOn = slice.PeriodEnd,
                // Whole days, floored - a renewal 18 hours out reads as "0 days", which is the honest
                // answer for a report someone scans for what needs attention today.
                DaysUntilRenewal = WholeDays(slice.PeriodEnd - now),
                ServerCount = serverCount,
                Quantity = slice.Quantity,
                CurrentAmount = slice.EarnedAmount,
                RenewalAmount = price?.AmountFor(renewalQuantity) ?? 0m,
                RenewalAmountUnknown = price is null,
                HasScheduledChange = appliesAtRenewal,
                ScheduledPlanName = appliesAtRenewal ? pending!.Plan.Name : null,
                ScheduledTermMonths = appliesAtRenewal ? pending!.TermMonths : null
            });
        }

        rows = rows.OrderBy(r => r.RenewsOn).ThenBy(r => r.OrganizationName, StringComparer.OrdinalIgnoreCase).ToList();

        var priced = rows.Where(r => !r.RenewalAmountUnknown).ToList();
        var unpriced = rows.Count - priced.Count;
        var scheduled = rows.Count(r => r.HasScheduledChange);

        var summary = new List<ReportSummaryValueDto>
        {
            Count("Renewing", rows.Count, "organization", "organizations"),
            new()
            {
                Label = "Expected revenue",
                Value = priced.Sum(r => r.RenewalAmount).ToString("C", Money),
                Detail = unpriced > 0 ? $"excludes {unpriced} unpriced" : "at current catalog prices"
            },
            new()
            {
                Label = "Changing at renewal",
                Value = scheduled.ToString("N0", Money),
                Detail = scheduled == 0 ? "no pending changes" : "plan or term change due"
            }
        };

        // The forecast deliberately excludes rows priced from a missing catalog entry rather than
        // counting them as $0 - a plan with no price for a term somebody is being billed on is a defect
        // that needs fixing before the renewal date, not a rounding difference.
        if (unpriced > 0)
        {
            summary.Add(new ReportSummaryValueDto
            {
                Label = "Unpriced",
                Value = unpriced.ToString("N0", Money),
                Detail = "plan has no price for the term"
            });
        }

        return Result(rows, summary, now, truncated);
    }

    // ---------------------------------------------------------------------------------------------
    // New signups
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ReportResult<NewSignupRowDto>> GetNewSignupsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var (fromInstant, toExclusive) = Window(from, to);

        var tenants = await dbContext.Set<Tenant>()
            .Where(t => t.CreatedOn >= fromInstant && t.CreatedOn < toExclusive)
            .OrderByDescending(t => t.CreatedOn)
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);

        var truncated = Trim(tenants);
        var tenantIds = tenants.Select(t => t.Id).ToList();

        var plans = await dbContext.Set<Subscription>()
            .Include(tp => tp.Plan)
            .Where(tp => tp.EndDate == null && tenantIds.Contains(tp.TenantId))
            .ToDictionaryAsync(tp => tp.TenantId, cancellationToken);

        var terms = await CurrentTermsAsync(tenantIds, cancellationToken);
        var serverCounts = await ServerCountsAsync(tenantIds, cancellationToken);

        // Bare IgnoreQueryFilters, deliberately - this needs to cross the tenant boundary AND see
        // soft-deleted rows, which is the one combination JumpStart declines to give a named helper
        // to precisely so it stands out here. Activation is "did they ever get a server running", and
        // an Organization that added one and later removed it did activate; counting only live servers
        // would quietly reclassify it as a signup that never started.
        var activations = await dbContext.Set<RustServer>()
            .IgnoreQueryFilters()
            .Where(s => tenantIds.Contains(s.TenantId))
            .GroupBy(s => s.TenantId)
            .Select(g => new { TenantId = g.Key, FirstOn = g.Min(s => s.CreatedOn) })
            .ToDictionaryAsync(x => x.TenantId, x => x.FirstOn, cancellationToken);

        var rows = tenants.Select(tenant =>
        {
            plans.TryGetValue(tenant.Id, out var plan);
            terms.TryGetValue(tenant.Id, out var term);
            var activatedOn = activations.TryGetValue(tenant.Id, out var first) ? first : (DateTimeOffset?)null;

            return new NewSignupRowDto
            {
                TenantId = tenant.Id,
                OrganizationName = tenant.Name,
                ContactEmail = tenant.ContactEmail,
                SignedUpOn = tenant.CreatedOn,
                DaysSinceSignup = WholeDays(now - tenant.CreatedOn),
                PlanName = plan?.Plan.Name ?? string.Empty,
                PlanColorCode = plan?.Plan.ColorCode ?? string.Empty,
                TermMonths = term?.TermMonths ?? 0,
                ServerCount = serverCounts.GetValueOrDefault(tenant.Id),
                ActivatedOn = activatedOn,
                DaysToActivate = activatedOn is { } on ? WholeDays(on - tenant.CreatedOn) : null,
                IsActive = tenant.IsActive
            };
        }).ToList();

        var activated = rows.Count(r => r.ActivatedOn is not null);
        var paid = rows.Count(r => r.ServerCount > 0);

        var summary = new List<ReportSummaryValueDto>
        {
            Count("Signups", rows.Count, "organization", "organizations"),
            new()
            {
                Label = "Activated",
                Value = activated.ToString("N0", Money),
                Detail = rows.Count == 0
                    ? "no signups in this window"
                    : $"{(decimal)activated / rows.Count:P0} added a server"
            },
            new()
            {
                Label = "Median days to activate",
                Value = Median(rows.Where(r => r.DaysToActivate is not null).Select(r => r.DaysToActivate!.Value)),
                Detail = "signup to first server"
            },
            new()
            {
                Label = "Still empty",
                Value = (rows.Count - paid).ToString("N0", Money),
                Detail = "no servers right now"
            }
        };

        return Result(rows, summary, now, truncated);
    }

    // ---------------------------------------------------------------------------------------------
    // Subscription register
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ReportResult<SubscriptionRegisterRowDto>> GetSubscriptionsAsync(
        Guid? planId = null, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var plans = await dbContext.Set<Subscription>()
            .Include(tp => tp.Tenant)
            .Include(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(tp => tp.EndDate == null
                         && tp.Tenant.DeletedOn == null
                         && (planId == null || tp.PlanId == planId))
            .OrderBy(tp => tp.StartDate)
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);

        var truncated = Trim(plans);
        var tenantIds = plans.Select(tp => tp.TenantId).ToList();

        var terms = await CurrentTermsAsync(tenantIds, cancellationToken);
        var serverCounts = await ServerCountsAsync(tenantIds, cancellationToken);
        var pendingChanges = await PendingChangesAsync(tenantIds, cancellationToken);

        var rows = plans.Select(tp =>
        {
            terms.TryGetValue(tp.TenantId, out var term);
            var serverCount = serverCounts.GetValueOrDefault(tp.TenantId);
            var termMonths = term?.TermMonths ?? BillingTerms.Monthly;
            var quantity = term?.Quantity ?? 1;
            var price = tp.Plan.Prices.FirstOrDefault(p => p.TermMonths == termMonths);

            return new SubscriptionRegisterRowDto
            {
                TenantId = tp.TenantId,
                OrganizationName = tp.Tenant.Name,
                ContactEmail = tp.Tenant.ContactEmail,
                PlanId = tp.PlanId,
                PlanName = tp.Plan.Name,
                PlanColorCode = tp.Plan.ColorCode,
                TermMonths = termMonths,
                PlanSince = tp.StartDate,
                DaysOnPlan = WholeDays(now - tp.StartDate),
                PeriodEnd = term?.PeriodEnd ?? default,
                ServerCount = serverCount,
                Quantity = quantity,
                MaximumServers = tp.Plan.MaximumServers,
                PeriodAmount = term?.EarnedAmount ?? 0m,
                // Run rate is what they pay for, which is the capacity they bought - not how much of it
                // they happen to be using.
                MonthlyValue = price?.MonthlyEquivalentFor(quantity) ?? 0m,
                MonthlyValueUnknown = price is null,
                HasScheduledChange = pendingChanges.ContainsKey(tp.TenantId),
                IsActive = tp.Tenant.IsActive
            };
        })
        .OrderByDescending(r => r.MonthlyValue)
        .ThenBy(r => r.OrganizationName, StringComparer.OrdinalIgnoreCase)
        .ToList();

        var priced = rows.Where(r => !r.MonthlyValueUnknown).ToList();
        var paying = priced.Count(r => r.MonthlyValue > 0m);

        // The plan mix, as one line rather than a chart - "Stone 12 · Metal 4 · Wood 31". A grid report
        // can carry its own aggregate this cheaply; a chart for it can come later if the list gets long.
        var mix = string.Join(" · ", rows
            .GroupBy(r => r.PlanName)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}"));

        var summary = new List<ReportSummaryValueDto>
        {
            Count("Subscriptions", rows.Count, "organization", "organizations"),
            new()
            {
                Label = "MRR",
                Value = priced.Sum(r => r.MonthlyValue).ToString("C", Money),
                Detail = rows.Count - priced.Count > 0
                    ? $"excludes {rows.Count - priced.Count} unpriced"
                    : "monthly run rate at list prices"
            },
            new()
            {
                Label = "Paying",
                Value = paying.ToString("N0", Money),
                Detail = $"{rows.Count - paying} on free plans"
            },
            new()
            {
                Label = "Servers",
                Value = rows.Sum(r => r.ServerCount).ToString("N0", Money),
                Detail = string.IsNullOrEmpty(mix) ? "across all plans" : mix
            }
        };

        return Result(rows, summary, now, truncated);
    }

    // ---------------------------------------------------------------------------------------------
    // Plan changes
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ReportResult<PlanChangeRowDto>> GetPlanChangesAsync(
        DateOnly from, DateOnly to, Guid? planId = null, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var (fromInstant, toExclusive) = Window(from, to);

        // An interval that opens where another one closed is a move. The very first interval of an
        // Organization's life has nothing before it - that is a signup, and belongs on that report.
        var opened = await dbContext.Set<Subscription>()
            .Include(tp => tp.Tenant)
            .Include(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(tp => tp.StartDate >= fromInstant
                         && tp.StartDate < toExclusive
                         && tp.Tenant.DeletedOn == null
                         && dbContext.Set<Subscription>()
                             .Any(prev => prev.TenantId == tp.TenantId && prev.EndDate == tp.StartDate))
            .OrderByDescending(tp => tp.StartDate)
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);

        var truncated = Trim(opened);
        var tenantIds = opened.Select(tp => tp.TenantId).Distinct().ToList();

        // Every interval belonging to the affected Organizations, so each move can be paired with the
        // one it closed. Fetched whole rather than joined pairwise: the set of Organizations that
        // changed plan inside one window is small, and their full history is smaller still.
        var history = await dbContext.Set<Subscription>()
            .Include(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(tp => tenantIds.Contains(tp.TenantId))
            .ToListAsync(cancellationToken);

        var byTenant = history.ToLookup(tp => tp.TenantId);

        // Compared at the capacity the tenant holds now, not at their server count - a per-unit plan
        // prices per slot bought, so ranking two plans at the wrong quantity would misclassify the
        // direction of the move.
        var terms = await CurrentTermsAsync(tenantIds, cancellationToken);

        var rows = new List<PlanChangeRowDto>(opened.Count);
        foreach (var move in opened)
        {
            var previous = byTenant[move.TenantId].FirstOrDefault(tp => tp.EndDate == move.StartDate);
            if (previous is null)
            {
                continue;
            }

            var quantity = terms.TryGetValue(move.TenantId, out var term) ? term.Quantity : 1;
            var fromRate = BestMonthlyRate(previous.Plan, quantity);
            var toRate = BestMonthlyRate(move.Plan, quantity);

            rows.Add(new PlanChangeRowDto
            {
                TenantId = move.TenantId,
                OrganizationName = move.Tenant.Name,
                ContactEmail = move.Tenant.ContactEmail,
                ChangedOn = move.StartDate,
                FromPlanName = previous.Plan.Name,
                FromPlanColorCode = previous.Plan.ColorCode,
                ToPlanName = move.Plan.Name,
                ToPlanColorCode = move.Plan.ColorCode,
                Direction = (fromRate, toRate) switch
                {
                    (null, _) or (_, null) => PlanChangeDirection.Unknown,
                    var (f, t) when t > f => PlanChangeDirection.Upgrade,
                    var (f, t) when t < f => PlanChangeDirection.Downgrade,
                    _ => PlanChangeDirection.Lateral
                },
                MonthlyDelta = (toRate ?? 0m) - (fromRate ?? 0m),
                FromIsFree = fromRate == 0m,
                ToIsFree = toRate == 0m
            });
        }

        // Applied after pairing rather than in the query: the filter means "this plan was one side of
        // the move", and only one of those two sides is the row the query selected.
        if (planId is { } id)
        {
            var name = history.FirstOrDefault(tp => tp.PlanId == id)?.Plan.Name;
            rows = rows.Where(r => r.FromPlanName == name || r.ToPlanName == name).ToList();
        }

        var upgrades = rows.Count(r => r.Direction == PlanChangeDirection.Upgrade);
        var downgrades = rows.Count(r => r.Direction == PlanChangeDirection.Downgrade);
        var churned = rows.Count(r => r.ToIsFree && !r.FromIsFree);
        var converted = rows.Count(r => r.FromIsFree && !r.ToIsFree);

        var summary = new List<ReportSummaryValueDto>
        {
            Count("Changes", rows.Count, "plan move", "plan moves"),
            new()
            {
                Label = "Net monthly change",
                Value = rows.Sum(r => r.MonthlyDelta).ToString("C", Money),
                Detail = $"{upgrades} up · {downgrades} down"
            },
            new()
            {
                Label = "Converted to paid",
                Value = converted.ToString("N0", Money),
                Detail = "moved off a free plan"
            },
            new()
            {
                Label = "Dropped to free",
                Value = churned.ToString("N0", Money),
                Detail = "what churn looks like here"
            }
        };

        return Result(rows, summary, now, truncated);
    }

    // ---------------------------------------------------------------------------------------------
    // Scheduled changes
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ReportResult<ScheduledChangeRowDto>> GetScheduledChangesAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var (fromInstant, toExclusive) = Window(from, to);

        var pending = await dbContext.Set<ScheduledPlanChange>()
            .Include(c => c.Tenant)
            .Include(c => c.Plan).ThenInclude(p => p.Prices)
            .Where(c => c.AppliedOn == null
                        && c.CancelledOn == null
                        && c.Tenant.DeletedOn == null
                        && c.EffectiveDate >= fromInstant
                        && c.EffectiveDate < toExclusive)
            .OrderBy(c => c.EffectiveDate)
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);

        var truncated = Trim(pending);
        var tenantIds = pending.Select(c => c.TenantId).Distinct().ToList();

        var current = await dbContext.Set<Subscription>()
            .Include(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(tp => tp.EndDate == null && tenantIds.Contains(tp.TenantId))
            .ToDictionaryAsync(tp => tp.TenantId, cancellationToken);

        var terms = await CurrentTermsAsync(tenantIds, cancellationToken);
        var serverCounts = await ServerCountsAsync(tenantIds, cancellationToken);

        var rows = new List<ScheduledChangeRowDto>(pending.Count);
        foreach (var change in pending)
        {
            if (!current.TryGetValue(change.TenantId, out var currentPlan))
            {
                // Every Organization is supposed to have exactly one open interval. If one somehow
                // doesn't, skipping is better than inventing a "from" side for the comparison.
                continue;
            }

            terms.TryGetValue(change.TenantId, out var term);
            var currentTerm = term?.TermMonths ?? BillingTerms.Monthly;

            // Both terms and both quantities are known here, so the comparison uses the exact prices
            // rather than each plan's best rate - this is what these two subscriptions will actually
            // cost per month, including a capacity release that is part of the same change.
            var fromRate = MonthlyRate(currentPlan.Plan, currentTerm, term?.Quantity ?? 1);
            var toRate = MonthlyRate(change.Plan, change.TermMonths, change.Quantity);

            rows.Add(new ScheduledChangeRowDto
            {
                TenantId = change.TenantId,
                OrganizationName = change.Tenant.Name,
                ContactEmail = change.Tenant.ContactEmail,
                CurrentPlanName = currentPlan.Plan.Name,
                CurrentPlanColorCode = currentPlan.Plan.ColorCode,
                CurrentTermMonths = currentTerm,
                ScheduledPlanName = change.Plan.Name,
                ScheduledPlanColorCode = change.Plan.ColorCode,
                ScheduledTermMonths = change.TermMonths,
                AcceptedOn = change.CreatedOn,
                EffectiveDate = change.EffectiveDate,
                DaysUntilEffective = WholeDays(change.EffectiveDate - now),
                MonthlyDelta = (toRate ?? 0m) - (fromRate ?? 0m),
                ScheduledIsFree = toRate == 0m
            });
        }

        var losses = rows.Where(r => r.MonthlyDelta < 0m).ToList();

        var summary = new List<ReportSummaryValueDto>
        {
            Count("Pending", rows.Count, "change", "changes"),
            new()
            {
                Label = "Net monthly change",
                Value = rows.Sum(r => r.MonthlyDelta).ToString("C", Money),
                Detail = "once all of these land"
            },
            new()
            {
                Label = "At risk",
                Value = losses.Sum(r => -r.MonthlyDelta).ToString("C", Money),
                Detail = $"across {losses.Count} downgrade(s)"
            },
            new()
            {
                Label = "Landing in 30 days",
                Value = rows.Count(r => r.DaysUntilEffective is >= 0 and <= 30).ToString("N0", Money),
                Detail = "still time to intervene"
            }
        };

        return Result(rows, summary, now, truncated);
    }

    // ---------------------------------------------------------------------------------------------
    // Receivables
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ReportResult<ReceivableRowDto>> GetReceivablesAsync(
        decimal minOutstanding = 0m, bool overdueOnly = false, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var rows = await OpenReceivablesAsync(now, cancellationToken);

        var truncated = rows.Count > MaxRows;
        rows = rows
            .Where(r => r.Outstanding >= minOutstanding && (!overdueOnly || r.DaysOverdue > 0))
            .OrderByDescending(r => r.DaysOverdue)
            .ThenByDescending(r => r.Outstanding)
            .ToList();
        Trim(rows);

        var overdue = rows.Where(r => r.DaysOverdue > 0).ToList();

        var summary = new List<ReportSummaryValueDto>
        {
            new()
            {
                Label = "Outstanding",
                Value = rows.Sum(r => r.Outstanding).ToString("C", Money),
                Detail = Count(rows.Count, "open invoice", "open invoices")
            },
            new()
            {
                Label = "Overdue",
                Value = overdue.Sum(r => r.Outstanding).ToString("C", Money),
                Detail = Count(overdue.Count, "invoice past due", "invoices past due")
            },
            new()
            {
                Label = "Over 90 days",
                Value = rows.Where(r => r.Bucket == AgingBucket.Over90).Sum(r => r.Outstanding).ToString("C", Money),
                Detail = "usually written off"
            },
            new()
            {
                Label = "Aging",
                Value = overdue.Count == 0 ? "—" : $"{overdue.Max(r => r.DaysOverdue)}d",
                // The aged breakdown as one line, in the order an accountant reads it. To the cent, not
                // rounded to whole dollars: a money report that displays $4.95 as $5 spends more trust
                // than the shorter line is worth.
                Detail = string.Join(" · ", new[]
                {
                    $"cur {Bucketed(rows, AgingBucket.Current).ToString("C", Money)}",
                    $"1-30 {Bucketed(rows, AgingBucket.Days1To30).ToString("C", Money)}",
                    $"31-60 {Bucketed(rows, AgingBucket.Days31To60).ToString("C", Money)}",
                    $"61-90 {Bucketed(rows, AgingBucket.Days61To90).ToString("C", Money)}",
                    $"90+ {Bucketed(rows, AgingBucket.Over90).ToString("C", Money)}"
                })
            }
        };

        return Result(rows, summary, now, truncated);
    }

    /// <inheritdoc />
    public async Task<ReportResult<DelinquentAccountRowDto>> GetDelinquentAccountsAsync(
        decimal minOutstanding = 0m, bool overdueOnly = true, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var receivables = await OpenReceivablesAsync(now, cancellationToken);

        var tenantIds = receivables.Select(r => r.TenantId).Distinct().ToList();
        var terms = await CurrentTermsAsync(tenantIds, cancellationToken);

        var plans = await dbContext.Set<Subscription>()
            .Include(s => s.Tenant)
            .Include(s => s.Plan).ThenInclude(p => p.Prices)
            .Where(s => s.EndDate == null && tenantIds.Contains(s.TenantId))
            .ToDictionaryAsync(s => s.TenantId, cancellationToken);

        var rows = receivables
            .GroupBy(r => r.TenantId)
            .Select(g =>
            {
                var first = g.First();
                plans.TryGetValue(g.Key, out var subscription);
                terms.TryGetValue(g.Key, out var term);

                var overdue = g.Where(r => r.DaysOverdue > 0).ToList();
                var quantity = term?.Quantity ?? 1;
                var price = subscription?.Plan.Prices
                    .FirstOrDefault(p => p.TermMonths == (term?.TermMonths ?? BillingTerms.Monthly));

                return new DelinquentAccountRowDto
                {
                    TenantId = g.Key,
                    OrganizationName = first.OrganizationName,
                    ContactEmail = first.ContactEmail,
                    PlanName = first.PlanName,
                    PlanColorCode = first.PlanColorCode,
                    OpenInvoices = g.Count(),
                    TotalOutstanding = g.Sum(r => r.Outstanding),
                    OverdueAmount = overdue.Sum(r => r.Outstanding),
                    OldestOverdueDays = overdue.Count == 0 ? 0 : overdue.Max(r => r.DaysOverdue),
                    WorstBucket = g.Max(r => r.Bucket),
                    CurrentAmount = Bucketed(g, AgingBucket.Current),
                    Days1To30 = Bucketed(g, AgingBucket.Days1To30),
                    Days31To60 = Bucketed(g, AgingBucket.Days31To60),
                    Days61To90 = Bucketed(g, AgingBucket.Days61To90),
                    Over90 = Bucketed(g, AgingBucket.Over90),
                    MonthlyValue = price?.MonthlyEquivalentFor(quantity) ?? 0m,
                    IsActive = subscription?.Tenant.IsActive ?? true
                };
            })
            .Where(r => r.TotalOutstanding >= minOutstanding && (!overdueOnly || r.OverdueAmount > 0m))
            // Oldest debt first, then largest - the order somebody works down the list in.
            .OrderByDescending(r => r.OldestOverdueDays)
            .ThenByDescending(r => r.OverdueAmount)
            .ToList();

        var truncated = Trim(rows);

        var summary = new List<ReportSummaryValueDto>
        {
            Count("Accounts", rows.Count, "organization", "organizations"),
            new()
            {
                Label = "Overdue",
                Value = rows.Sum(r => r.OverdueAmount).ToString("C", Money),
                Detail = $"of {rows.Sum(r => r.TotalOutstanding).ToString("C", Money)} outstanding"
            },
            new()
            {
                Label = "Oldest",
                Value = rows.Count == 0 ? "—" : $"{rows.Max(r => r.OldestOverdueDays)}d",
                Detail = "since falling due"
            },
            new()
            {
                // What walks out of the door if every one of these accounts is lost - the figure that
                // decides how much effort collections is worth.
                Label = "Revenue at risk",
                Value = rows.Sum(r => r.MonthlyValue).ToString("C", Money),
                Detail = "per month, if all churn"
            }
        };

        return Result(rows, summary, now, truncated);
    }

    /// <summary>
    /// Every open invoice with a balance, as flat rows - the shared basis for both collections reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Open is the only status that can be outstanding: a paid invoice has no balance, a voided one was
    /// never owed, and an uncollectible one has already been given up on and belongs in a bad-debt
    /// figure rather than in receivables. The partial index behind this query filters on exactly that.
    /// </para>
    /// <para>
    /// Aging is computed here rather than stored - an invoice does not change as it ages, so a stored
    /// bucket would be stale the day after it was written.
    /// </para>
    /// </remarks>
    private async Task<List<ReceivableRowDto>> OpenReceivablesAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var invoices = await dbContext.Set<Invoice>()
            .Include(i => i.Tenant)
            .Where(i => i.Status == InvoiceStatus.Open && i.Tenant.DeletedOn == null)
            .OrderBy(i => i.DueOn)
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);

        var tenantIds = invoices.Select(i => i.TenantId).Distinct().ToList();

        var plans = await dbContext.Set<Subscription>()
            .Include(s => s.Plan)
            .Where(s => s.EndDate == null && tenantIds.Contains(s.TenantId))
            .ToDictionaryAsync(s => s.TenantId, cancellationToken);

        return invoices
            // An Open invoice settled to zero is a data inconsistency rather than a receivable - the
            // status should have moved to Paid. Excluded from the money rather than reported as a
            // zero-balance debt somebody would waste time chasing.
            .Where(i => i.AmountOutstanding > 0m)
            .Select(i =>
            {
                plans.TryGetValue(i.TenantId, out var subscription);
                var daysOverdue = i.DueOn is { } due ? WholeDays(now - due) : 0;

                return new ReceivableRowDto
                {
                    TenantId = i.TenantId,
                    InvoiceId = i.Id,
                    OrganizationName = i.Tenant.Name,
                    ContactEmail = i.Tenant.ContactEmail,
                    InvoiceNumber = i.Number ?? string.Empty,
                    Currency = i.Currency,
                    IssuedOn = i.IssuedOn,
                    DueOn = i.DueOn,
                    DaysOverdue = daysOverdue,
                    Bucket = BucketFor(daysOverdue),
                    Total = i.Total,
                    AmountPaid = i.AmountPaid,
                    AmountCredited = i.AmountCredited,
                    Outstanding = i.AmountOutstanding,
                    IsPartiallyPaid = i.AmountPaid > 0m && i.AmountPaid < i.Total,
                    PlanName = subscription?.Plan.Name ?? string.Empty,
                    PlanColorCode = subscription?.Plan.ColorCode ?? string.Empty
                };
            })
            .ToList();
    }

    private static AgingBucket BucketFor(int daysOverdue) => daysOverdue switch
    {
        <= 0 => AgingBucket.Current,
        <= 30 => AgingBucket.Days1To30,
        <= 60 => AgingBucket.Days31To60,
        <= 90 => AgingBucket.Days61To90,
        _ => AgingBucket.Over90
    };

    private static decimal Bucketed(IEnumerable<ReceivableRowDto> rows, AgingBucket bucket) =>
        rows.Where(r => r.Bucket == bucket).Sum(r => r.Outstanding);

    // ---------------------------------------------------------------------------------------------
    // Filter options
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportFilterOptionDto>> GetPlanFilterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var subscribed = dbContext.Set<Subscription>().Select(tp => tp.PlanId);

        var plans = await dbContext.Set<Plan>()
            .Where(p => p.Active || subscribed.Contains(p.Id))
            .OrderBy(p => p.Name)
            .ThenByDescending(p => p.CreatedOn)
            .ToListAsync(cancellationToken);

        return plans.Select(p => new ReportFilterOptionDto
        {
            Value = p.Id.ToString(),
            Label = p.Active ? p.Name : $"{p.Name} (retired)",
            Group = p.Active ? "Active" : "Retired"
        }).ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------------------------------------

    /// <summary>Turns an inclusive pair of UTC dates into a half-open instant range.</summary>
    /// <remarks>
    /// Half-open at the top: <paramref name="to"/> is an inclusive date, so the boundary is the start of
    /// the day after it. Comparing against the end of that day instead would drop anything timed at
    /// midnight.
    /// </remarks>
    private static (DateTimeOffset From, DateTimeOffset ToExclusive) Window(DateOnly from, DateOnly to) =>
        (new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
         new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    /// <summary>
    /// Drops the sentinel row queries fetch past <see cref="MaxRows"/>, and reports whether there was
    /// one. Every report asks for one row more than it will return, which is how "there are more"
    /// is known without a second count query.
    /// </summary>
    private static bool Trim<T>(List<T> items)
    {
        if (items.Count <= MaxRows)
        {
            return false;
        }

        items.RemoveRange(MaxRows, items.Count - MaxRows);
        return true;
    }

    private ReportResult<TRow> Result<TRow>(
        List<TRow> rows, List<ReportSummaryValueDto> summary, DateTimeOffset generatedOn, bool truncated) =>
        new()
        {
            Rows = rows,
            Summary = summary,
            GeneratedOn = generatedOn,
            Truncated = truncated,
            RowLimit = MaxRows
        };

    /// <summary>
    /// Live server counts per Organization.
    /// </summary>
    /// <remarks>
    /// <c>AcrossAllTenants</c> because <see cref="RustServer"/> is tenant-scoped and a report request has
    /// no tenant of its own - without it the filter reduces every count to zero, which would silently
    /// misprice every per-server plan. Soft-deleted servers stay excluded, which is what "how many do
    /// they have" means.
    /// </remarks>
    private async Task<Dictionary<Guid, int>> ServerCountsAsync(
        List<Guid> tenantIds, CancellationToken cancellationToken) =>
        await dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .Where(s => tenantIds.Contains(s.TenantId))
            .GroupBy(s => s.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, cancellationToken);

    /// <summary>The pending change per Organization, keyed by tenant. At most one each.</summary>
    private async Task<Dictionary<Guid, ScheduledPlanChange>> PendingChangesAsync(
        List<Guid> tenantIds, CancellationToken cancellationToken) =>
        await dbContext.Set<ScheduledPlanChange>()
            .Include(c => c.Plan).ThenInclude(p => p.Prices)
            .Where(c => c.AppliedOn == null && c.CancelledOn == null && tenantIds.Contains(c.TenantId))
            .ToDictionaryAsync(c => c.TenantId, cancellationToken);

    /// <summary>
    /// The billing period currently in force for each Organization.
    /// </summary>
    /// <remarks>
    /// "Current" is the last slice of the latest period - the one whose <c>EndDate</c> reaches its
    /// <c>PeriodEnd</c>, with no later period on the same subscription. Expressed as a NOT EXISTS rather
    /// than a GroupBy so the database returns one row per Organization instead of every period it has
    /// ever been billed for.
    /// </remarks>
    private async Task<Dictionary<Guid, SubscriptionPeriod>> CurrentTermsAsync(
        List<Guid> tenantIds, CancellationToken cancellationToken)
    {
        var terms = await dbContext.Set<SubscriptionPeriod>()
            .Include(t => t.Subscription)
            .Where(t => t.Subscription.EndDate == null
                        && tenantIds.Contains(t.Subscription.TenantId)
                        && t.EndDate == t.PeriodEnd
                        && !dbContext.Set<SubscriptionPeriod>()
                            .Any(later => later.SubscriptionId == t.SubscriptionId && later.PeriodEnd > t.PeriodEnd))
            .ToListAsync(cancellationToken);

        return terms
            .GroupBy(t => t.Subscription.TenantId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.PeriodEnd).First());
    }

    /// <summary>
    /// The best monthly rate a plan is sold at, across every term it offers, or <c>null</c> when it has
    /// no prices at all.
    /// </summary>
    /// <remarks>
    /// Used to rank two plans against each other when the terms involved aren't comparable - a plan sold
    /// only annually and one sold only monthly still have to be orderable. This is the same measure
    /// <c>SubscriptionService.GetPlanOptionsAsync</c> sorts the plan catalog by, so "upgrade" means the
    /// same thing on this report as it does on the screen where the customer chose.
    /// </remarks>
    private static decimal? BestMonthlyRate(Plan plan, int quantity) =>
        plan.Prices.Count == 0 ? null : plan.Prices.Min(p => p.MonthlyEquivalentFor(quantity));

    /// <summary>The monthly rate for one specific term, or <c>null</c> when the plan doesn't offer it.</summary>
    private static decimal? MonthlyRate(Plan plan, int termMonths, int quantity) =>
        plan.Prices.FirstOrDefault(p => p.TermMonths == termMonths)?.MonthlyEquivalentFor(quantity);

    /// <summary>Whole days, floored - "in 0 days" for something 18 hours out.</summary>
    private static int WholeDays(TimeSpan span) => (int)Math.Floor(span.TotalDays);

    private static string Count(int value, string singular, string plural) =>
        $"{value.ToString("N0", Money)} {(value == 1 ? singular : plural)}";

    private static ReportSummaryValueDto Count(string label, int value, string singular, string plural) =>
        new()
        {
            Label = label,
            Value = value.ToString("N0", Money),
            Detail = value == 1 ? singular : plural
        };

    /// <summary>
    /// The median of a set of day counts, or a dash when there are none.
    /// </summary>
    /// <remarks>
    /// Median rather than mean: one Organization that signed up and activated eleven months later drags
    /// an average far enough to make it useless, and the question being asked is what a typical signup
    /// does.
    /// </remarks>
    private static string Median(IEnumerable<int> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        if (ordered.Count == 0)
        {
            return "—";
        }

        var middle = ordered.Count / 2;
        var median = ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2m;

        return median.ToString("0.#", Money);
    }
}
