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
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <inheritdoc cref="ISubscriptionService" />
public class SubscriptionService(
    ApiDbContext dbContext,
    ISubscriptionRepository subscriptionRepository,
    IInvoiceService invoiceService,
    Administration.IRoleCompressionService compression,
    TimeProvider timeProvider) : ISubscriptionService
{
    /// <inheritdoc />
    public async Task<SubscriptionDto?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var plan = await subscriptionRepository.GetForTenantAsync(tenantId, cancellationToken);
        var slice = await subscriptionRepository.GetCurrentTermAsync(tenantId, cancellationToken);

        if (plan is null || slice is null)
        {
            return null;
        }

        var pending = await GetPendingChangeAsync(tenantId, cancellationToken);
        var serverCount = await CountServersAsync(tenantId, cancellationToken);

        // The price row in force for this period. Its UnitAmount is what decides whether capacity is
        // something the user can buy at all - see ResolveQuantity.
        var price = plan.Plan.Prices.FirstOrDefault(p => p.TermMonths == slice.TermMonths);
        var included = price?.IncludedUnits ?? 0;

        return new SubscriptionDto
        {
            PlanId = plan.PlanId,
            PlanName = plan.Plan.Name,
            PlanColorCode = plan.Plan.ColorCode,
            TermMonths = slice.TermMonths,
            PlanSince = plan.StartDate,
            PeriodStart = slice.PeriodStart,
            PeriodEnd = slice.PeriodEnd,
            PeriodAmount = slice.EarnedAmount,
            MaximumServers = plan.Plan.MaximumServers,
            CurrentServerCount = serverCount,
            Quantity = slice.Quantity,
            CanChangeQuantity = price is { UnitAmount: > 0m },
            IncludedUnits = included,
            UnitAmount = price?.UnitAmount ?? 0m,
            // Can't drop below what's running, and can't drop below what the plan bundles - those are
            // paid for inside the base amount whether they're used or not.
            MinimumQuantity = Math.Max(serverCount, included),
            ScheduledChange = pending is null
                ? null
                : new ScheduledPlanChangeDto
                {
                    PlanId = pending.PlanId,
                    PlanName = pending.Plan.Name,
                    TermMonths = pending.TermMonths,
                    Quantity = pending.Quantity,
                    EffectiveDate = pending.EffectiveDate
                }
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BillingHistoryEntryDto>> GetBillingHistoryAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var slices = await subscriptionRepository.GetBillingHistoryAsync(tenantId, cancellationToken);
        var sliceIds = slices.Select(s => s.Id).ToList();

        // The invoice covering each period, found through the line that points back at it. One query
        // for the lot rather than one per period, and keyed by period because that link is the only
        // thing tying the two bands together. Voided invoices are excluded on purpose: a document
        // issued in error should not appear against a period as though it were still owed.
        var invoicesByPeriod = await dbContext.Set<InvoiceLine>()
            .Include(l => l.Invoice)
            .Where(l => l.SubscriptionPeriodId != null
                        && sliceIds.Contains(l.SubscriptionPeriodId.Value)
                        && l.Invoice.Status != Shared.DTOs.InvoiceStatus.Void)
            .ToDictionaryAsync(l => l.SubscriptionPeriodId!.Value, l => l.Invoice, cancellationToken);

        return slices.Select(s =>
        {
            invoicesByPeriod.TryGetValue(s.Id, out var invoice);

            return new BillingHistoryEntryDto
            {
                PlanName = s.Subscription.Plan.Name,
                TermMonths = s.TermMonths,
                Quantity = s.Quantity,
                StartDate = s.StartDate,
                EndDate = s.EndDate,
                Amount = s.EarnedAmount,
                IsWholePeriod = s.IsWholePeriod,
                InvoiceNumber = invoice?.Number,
                InvoicedOn = invoice?.IssuedOn,
                DueOn = invoice?.DueOn,
                InvoiceStatus = invoice?.Status,
                Outstanding = invoice?.AmountOutstanding ?? 0m
            };
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlanOptionDto>> GetPlanOptionsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var current = await subscriptionRepository.GetForTenantAsync(tenantId, cancellationToken);
        var serverCount = await CountServersAsync(tenantId, cancellationToken);

        var plans = await dbContext.Set<Plan>()
            .Include(p => p.Prices)
            .Where(p => p.Active)
            .ToListAsync(cancellationToken);

        // Cheapest per-month rate first, evaluated in memory - "cheapest" is a min over each plan's
        // price rows now, not a column, and the active catalog is small enough that it doesn't matter.
        plans = plans
            .OrderBy(p => p.Prices.Count == 0 ? decimal.MaxValue : p.Prices.Min(pp => pp.MonthlyEquivalentFor(1)))
            .ToList();

        // A tenant sitting on a superseded (now-inactive) plan still needs to see it, or their own
        // current plan would silently vanish from the screen showing their subscription. It's shown as
        // current and can be stayed on; it just isn't something anyone can newly move to.
        if (current is not null && plans.All(p => p.Id != current.PlanId))
        {
            plans.Insert(0, current.Plan);
        }

        return plans.Select(p => new PlanOptionDto
        {
            Id = p.Id,
            Name = p.Name,
            ColorCode = p.ColorCode,
            PricingModel = p.PricingModel,
            Prices = p.Prices
                .OrderBy(pp => pp.TermMonths)
                .Select(pp => new PlanPriceDto
                {
                    TermMonths = pp.TermMonths,
                    BaseAmount = pp.BaseAmount,
                    IncludedUnits = pp.IncludedUnits,
                    UnitAmount = pp.UnitAmount,
                    Currency = pp.Currency
                }).ToList(),
            MaximumServers = p.MaximumServers,
            MaximumUsers = p.MaximumUsers,
            RetentionHistory = p.RetentionHistory,
            HasRoles = p.HasRoles,
            OnePerOwner = p.OnePerOwner,
            IsCurrent = current is not null && p.Id == current.PlanId,
            // Null means no ceiling, so there is nothing to exceed. Written out rather than left as
            // `p.MaximumServers >= serverCount`, which is a trap: a lifted comparison against null is
            // false, so every per-unit plan came back "not allowed" and could never be selected. The
            // two guards that use `<` are correct for the opposite reason - null < n is also false -
            // which is exactly why this one inverted silently while those didn't.
            AllowedForCurrentServerCount = p.MaximumServers is null || p.MaximumServers.Value >= serverCount
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<PlanChangeQuoteDto> QuoteAsync(
        Guid tenantId, Guid targetPlanId, int targetTermMonths, int? targetQuantity = null,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildQuoteAsync(tenantId, targetPlanId, targetTermMonths, targetQuantity, cancellationToken);
        return context.Quote;
    }

    /// <inheritdoc />
    public async Task<PlanChangeQuoteDto> ApplyAsync(
        Guid tenantId, Guid targetPlanId, int targetTermMonths, int? targetQuantity = null,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildQuoteAsync(tenantId, targetPlanId, targetTermMonths, targetQuantity, cancellationToken);

        if (!context.Quote.Allowed || context.Quote.IsNoOp || context.Outcome is null)
        {
            return context.Quote;
        }

        var now = timeProvider.GetUtcNow();
        var outcome = context.Outcome;
        var currentPlan = context.CurrentPlan!;
        var currentSlice = context.CurrentSlice!;

        // Everything below is one unit of work: closing a plan interval without opening its replacement
        // would leave the tenant planless, and re-slicing a billing period halfway would leave the
        // period's amounts not summing to what was charged.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Supersede rather than stack. Only one queued change is allowed per tenant (partial unique
        // index), and re-deciding is normal - someone who scheduled a downgrade and changed their mind
        // gets their new intent recorded, not a conflict. Saved before the new row is added, or the
        // index would reject the insert while the superseded row is still pending.
        var superseded = await GetPendingChangeAsync(tenantId, cancellationToken);
        if (superseded is not null)
        {
            superseded.CancelledOn = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (outcome.HasScheduledChange)
        {
            dbContext.Set<ScheduledPlanChange>().Add(new ScheduledPlanChange
            {
                TenantId = tenantId,
                PlanId = outcome.ScheduledPlan!.Id,
                TermMonths = outcome.ScheduledTermMonths!.Value,
                Quantity = outcome.ScheduledQuantity!.Value,
                EffectiveDate = outcome.ScheduledEffectiveDate!.Value,
                CreatedOn = now
            });
        }

        if (outcome.HasImmediateChange)
        {
            // Close the open billing slice at the change instant and re-price it for the shorter span
            // it actually covered. The reconciled amount comes from the calculator so the period's
            // slices still sum to everything charged against it - see Outcome.ClosingSliceAmount.
            currentSlice.EndDate = now;
            currentSlice.EarnedAmount = outcome.ClosingSliceAmount;

            var owningPlanId = currentSlice.SubscriptionId;

            if (outcome.ImmediatePlan.Id != currentPlan.PlanId)
            {
                // The plan itself changed, so the plan-history interval closes and a new one opens -
                // contiguously, at the same instant. Saved before the insert for the same reason the
                // superseded change was: the partial unique index allows only one open row per tenant,
                // so the old one has to be closed first.
                currentPlan.EndDate = now;
                await dbContext.SaveChangesAsync(cancellationToken);

                var openedPlan = new Subscription
                {
                    TenantId = tenantId,
                    PlanId = outcome.ImmediatePlan.Id,
                    StartDate = now
                };
                dbContext.Set<Subscription>().Add(openedPlan);
                await dbContext.SaveChangesAsync(cancellationToken);

                owningPlanId = openedPlan.Id;
            }

            // The new slice runs to the end of the same billing period, carrying that period's anchor -
            // a term change moves PeriodEnd out, but PeriodStart never moves, which is what stops
            // repeated upgrades from walking the renewal date forward.
            var openedSlice = new SubscriptionPeriod
            {
                SubscriptionId = owningPlanId,
                TermMonths = outcome.ImmediateTermMonths,
                Quantity = outcome.ImmediateQuantity,
                PeriodStart = currentSlice.PeriodStart,
                PeriodEnd = outcome.PeriodEndAfterChange,
                StartDate = now,
                EndDate = outcome.PeriodEndAfterChange,
                EarnedAmount = outcome.NewSliceAmount
            };
            dbContext.Set<SubscriptionPeriod>().Add(openedSlice);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Decision 01: the prorated difference is invoiced on its own document, immediately, rather
            // than waiting to become a line on the next period's invoice. Billing only what's owed
            // *now* - not the new slice's earned amount - is the point: the tenant already paid for the
            // rest of this period at the old rate, and this collects the difference.
            //
            // Inside the same transaction as everything above, so the invoice number is released rather
            // than burned if any of it fails.
            if (outcome.AmountDueNow > 0m)
            {
                await invoiceService.IssueForPeriodAsync(
                    openedSlice,
                    outcome.AmountDueNow,
                    $"{outcome.ImmediatePlan.Name} - prorated to {Date(outcome.PeriodEndAfterChange)}",
                    cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return context.Quote;
    }

    /// <inheritdoc />
    public async Task<bool> CancelScheduledChangeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingChangeAsync(tenantId, cancellationToken);
        if (pending is null)
        {
            return false;
        }

        pending.CancelledOn = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Everything <see cref="ApplyAsync"/> needs, computed once by the shared quote path.</summary>
    private sealed record QuoteContext(
        PlanChangeQuoteDto Quote,
        Subscription? CurrentPlan,
        SubscriptionPeriod? CurrentSlice,
        PlanChangeCalculator.Outcome? Outcome);

    /// <summary>
    /// The shared path behind quoting and applying: resolve everything, run the guard, compute the
    /// outcome, and describe it.
    /// </summary>
    private async Task<QuoteContext> BuildQuoteAsync(
        Guid tenantId, Guid targetPlanId, int targetTermMonths, int? targetQuantity,
        CancellationToken cancellationToken)
    {
        var currentPlan = await subscriptionRepository.GetForTenantAsync(tenantId, cancellationToken);
        var currentSlice = await subscriptionRepository.GetCurrentTermAsync(tenantId, cancellationToken);

        if (currentPlan is null || currentSlice is null)
        {
            return new QuoteContext(
                Blocked("Your organization doesn't have an active subscription yet. Contact support.", null),
                null, null, null);
        }

        // Prices included deliberately - without them the plan prices at zero and every upgrade reads
        // as a downgrade. See SubscriptionRepository's note.
        var targetPlan = await dbContext.Set<Plan>()
            .Include(p => p.Prices)
            .FirstOrDefaultAsync(p => p.Id == targetPlanId, cancellationToken);

        // Only a currently-offered plan may be moved TO. Staying on one that has since been superseded
        // is fine and deliberate (see Plan's remarks); newly choosing one that isn't on sale isn't.
        if (targetPlan is null || (!targetPlan.Active && targetPlan.Id != currentPlan.PlanId))
        {
            return new QuoteContext(Blocked("That plan is no longer available.", currentSlice), currentPlan, currentSlice, null);
        }

        // A plan is sold on exactly the terms it has prices for, and nothing upstream was enforcing
        // that. Without this a tenant could be moved onto a plan/term pair with no price at all -
        // PriceFor returns zero for a missing term rather than throwing, so the subscription would
        // quietly renew at $0 forever. Reachable straight from the change screen, which lets a plan be
        // picked while a term it isn't sold on is selected.
        if (targetPlan.Prices.All(p => p.TermMonths != targetTermMonths))
        {
            var sold = targetPlan.Prices
                .OrderBy(p => p.TermMonths)
                .Select(p => BillingTerms.Describe(p.TermMonths))
                .ToList();

            return new QuoteContext(
                Blocked(
                    sold.Count == 0
                        ? $"{targetPlan.Name} has no pricing set up yet, so it can't be selected."
                        : $"{targetPlan.Name} isn't sold {BillingTerms.Describe(targetTermMonths)} - only {string.Join(" or ", sold)}. "
                          + "Change the billing term first.",
                    currentSlice),
                currentPlan, currentSlice, null);
        }

        // Rule 1: never leave a tenant holding more servers than their plan allows. Checked against the
        // target whichever direction the move is in, and re-checked when a scheduled change eventually
        // applies (see SubscriptionScheduleService) - a downgrade queued today can still be invalidated
        // by servers added between now and its effective date.
        var serverCount = await CountServersAsync(tenantId, cancellationToken);
        if (targetPlan.MaximumServers is { } ceiling && ceiling < serverCount)
        {
            return new QuoteContext(
                Blocked(
                    $"The {targetPlan.Name} plan allows up to {ceiling} server(s), and you currently have {serverCount}. "
                    + $"Remove {serverCount - ceiling} server(s) first, then change your plan.",
                    currentSlice),
                currentPlan, currentSlice, null);
        }

        // Rule 1b: a plan somebody may only have one Organization on cannot be reached by moving a
        // second one onto it. Enforced here as well as at creation because a rule applied only at
        // creation is not one - create on a paid tier, downgrade to the restricted one, repeat.
        // Skipped when the Organization is already on this plan, so a term-only change never trips
        // over it, and when no founder was recorded. See OnePerOwnerRule.
        if (targetPlan.Id != currentPlan.PlanId
            && await Administration.OnePerOwnerRule.FounderOfAsync(dbContext, tenantId, cancellationToken)
                is { } founderId
            && await Administration.OnePerOwnerRule.WouldExceedAsync(
                dbContext, founderId, targetPlan.Id, tenantId, cancellationToken))
        {
            return new QuoteContext(
                Blocked(
                    $"Only one organization per account can be on the {targetPlan.Name} plan, and "
                    + "another one of yours already is. Choose a different plan.",
                    currentSlice),
                currentPlan, currentSlice, null);
        }

        // Rule 2: entitlement can't be released below what's actually running. Distinct from Rule 1 -
        // that one is about the plan's ceiling, this one about slots the tenant chose to give up. A
        // plan move resolves to at least the server count on its own, so this only fires when capacity
        // itself is being reduced.
        var resolvedQuantity = ResolveQuantity(targetPlan, targetTermMonths, targetQuantity, serverCount);
        if (resolvedQuantity < serverCount)
        {
            return new QuoteContext(
                Blocked(
                    $"You have {serverCount} server(s) running and can't hold fewer than that many slots. "
                    + $"Remove {serverCount - resolvedQuantity} server(s) first, then release the capacity.",
                    currentSlice),
                currentPlan, currentSlice, null);
        }

        var outcome = PlanChangeCalculator.Compute(
            currentPlan.Plan, currentSlice, targetPlan, targetTermMonths, resolvedQuantity, timeProvider.GetUtcNow());

        var pending = await GetPendingChangeAsync(tenantId, cancellationToken);
        var quote = Describe(currentPlan.Plan, currentSlice, targetPlan, targetTermMonths, resolvedQuantity, outcome, pending);

        // Warn before they accept, not after it happens. Moving to a plan without role separation
        // collapses the organization onto the single built-in Owner role - everyone keeps their
        // access and in fact gains some, but the distinctions the customer drew between their people
        // disappear and every member can then spend money. The numbers are a forecast: the change is
        // deferred, and the set actually promoted is re-derived when it lands. See
        // IRoleCompressionService.
        if (!targetPlan.HasRoles)
        {
            var preview = await compression.PreviewAsync(tenantId, cancellationToken);

            quote.WillCompressRoles = preview.IsNeeded;
            quote.MembersToPromote = preview.MembersPromoted;
            quote.RolesToRemove = preview.RolesRemoved;
        }

        return new QuoteContext(quote, currentPlan, currentSlice, outcome);

        PlanChangeQuoteDto Blocked(string reason, SubscriptionPeriod? slice) => new()
        {
            Allowed = false,
            BlockedReason = reason,
            CurrentPlanName = currentPlan?.Plan.Name ?? string.Empty,
            CurrentTermMonths = slice?.TermMonths ?? BillingTerms.Monthly,
            CurrentQuantity = slice?.Quantity ?? 1,
            CurrentPeriodEnd = slice?.PeriodEnd ?? default,
            PeriodEndAfterChange = slice?.PeriodEnd ?? default
        };
    }

    private static int ResolveQuantity(Plan targetPlan, int targetTermMonths, int? requested, int serverCount) =>
        PlanChangeCalculator.ResolveQuantity(targetPlan, targetTermMonths, requested, serverCount);

    /// <summary>
    /// Turns a computed outcome into the quote a user actually reads - the structured fields plus the
    /// plain-English <see cref="PlanChangeQuoteDto.Effects"/> lines.
    /// </summary>
    /// <remarks>
    /// The wording carries more weight here than usual. Because plan and term move independently, part
    /// of what someone accepts can land months later - and in the worst case (downgrading while
    /// lengthening the term) the deferred part is pushed out by the very change being made in the same
    /// breath. Every effect names its own date explicitly rather than saying "later" or "at renewal",
    /// and the deferred line spells out that the current plan continues until then.
    /// </remarks>
    private static PlanChangeQuoteDto Describe(
        Plan currentPlan,
        SubscriptionPeriod currentSlice,
        Plan targetPlan,
        int targetTermMonths,
        int targetQuantity,
        PlanChangeCalculator.Outcome outcome,
        ScheduledPlanChange? pending)
    {
        var isNoOp = !outcome.HasImmediateChange
            && !outcome.HasScheduledChange
            && (pending is null
                || (pending.PlanId == targetPlan.Id
                    && pending.TermMonths == targetTermMonths
                    && pending.Quantity == targetQuantity));

        var quote = new PlanChangeQuoteDto
        {
            Allowed = true,
            IsNoOp = isNoOp,
            CurrentPlanName = currentPlan.Name,
            CurrentTermMonths = currentSlice.TermMonths,
            CurrentQuantity = currentSlice.Quantity,
            CurrentPeriodEnd = currentSlice.PeriodEnd,
            HasImmediateChange = outcome.HasImmediateChange,
            ImmediatePlanName = outcome.HasImmediateChange ? outcome.ImmediatePlan.Name : null,
            ImmediateTermMonths = outcome.HasImmediateChange ? outcome.ImmediateTermMonths : null,
            ImmediateQuantity = outcome.HasImmediateChange ? outcome.ImmediateQuantity : null,
            AmountDueNow = outcome.AmountDueNow,
            PeriodEndAfterChange = outcome.PeriodEndAfterChange,
            HasScheduledChange = outcome.HasScheduledChange,
            ScheduledPlanName = outcome.ScheduledPlan?.Name,
            ScheduledTermMonths = outcome.ScheduledTermMonths,
            ScheduledQuantity = outcome.ScheduledQuantity,
            ScheduledEffectiveDate = outcome.ScheduledEffectiveDate
        };

        if (isNoOp)
        {
            quote.Effects.Add($"You're already on {currentPlan.Name}, billed {Describe(currentSlice.TermMonths)}. Nothing would change.");
            return quote;
        }

        if (outcome.HasImmediateChange)
        {
            // Split from the capacity line below because a quantity-only purchase isn't a plan move, and
            // saying "you move to Stone" to somebody already on Stone reads as a mistake in the quote.
            if (outcome.ImmediatePlan.Id != currentPlan.Id || outcome.ImmediateTermMonths != currentSlice.TermMonths)
            {
                quote.Effects.Add(
                    $"Starting today you move to {outcome.ImmediatePlan.Name}, billed {Describe(outcome.ImmediateTermMonths)}.");
            }

            if (outcome.ImmediateQuantity != currentSlice.Quantity)
            {
                quote.Effects.Add(
                    $"Your capacity goes to {Slots(outcome.ImmediateQuantity)} today, up from {Slots(currentSlice.Quantity)}.");
            }

            quote.Effects.Add(outcome.AmountDueNow > 0
                ? $"You owe {Money(outcome.AmountDueNow)} now, prorated for the rest of the period ending {Date(outcome.PeriodEndAfterChange)}."
                : $"Nothing is owed now. Your period still ends {Date(outcome.PeriodEndAfterChange)}.");

            if (outcome.ImmediateTermMonths != currentSlice.TermMonths)
            {
                quote.Effects.Add(
                    $"Your billing period now runs to {Date(outcome.PeriodEndAfterChange)} instead of {Date(currentSlice.PeriodEnd)}.");
            }
        }

        if (outcome.HasScheduledChange)
        {
            var effective = outcome.ScheduledEffectiveDate!.Value;

            // What the subscription looks like between now and the effective date - the immediate part
            // where there was one, otherwise the untouched current state.
            var staysPlan = outcome.HasImmediateChange ? outcome.ImmediatePlan : currentPlan;
            var staysTerm = outcome.HasImmediateChange ? outcome.ImmediateTermMonths : currentSlice.TermMonths;
            var staysQuantity = outcome.HasImmediateChange ? outcome.ImmediateQuantity : currentSlice.Quantity;
            var staysOn = staysPlan.Name;

            if (outcome.ScheduledPlan!.Id != staysPlan.Id || outcome.ScheduledTermMonths!.Value != staysTerm)
            {
                quote.Effects.Add(
                    $"On {Date(effective)} you move to {outcome.ScheduledPlan.Name}, billed {Describe(outcome.ScheduledTermMonths!.Value)}.");
            }

            if (outcome.ScheduledQuantity is { } scheduledQuantity && scheduledQuantity != staysQuantity)
            {
                quote.Effects.Add(
                    $"On {Date(effective)} your capacity drops to {Slots(scheduledQuantity)}, from {Slots(staysQuantity)}.");
            }

            quote.Effects.Add(
                $"Until then you stay on {staysOn} ({Describe(staysTerm)}, {Slots(staysQuantity)}) and keep everything it allows.");
            quote.Effects.Add("This part is not refunded - you've already paid through the end of that period.");
        }

        if (pending is not null
            && (pending.PlanId != targetPlan.Id
                || pending.TermMonths != targetTermMonths
                || pending.Quantity != targetQuantity))
        {
            quote.Effects.Add(
                $"This replaces the change to {pending.Plan.Name} ({Describe(pending.TermMonths)}, {Slots(pending.Quantity)}) "
                + $"you had scheduled for {Date(pending.EffectiveDate)}.");
        }

        return quote;
    }

    private static string Slots(int quantity) => quantity == 1 ? "1 server slot" : $"{quantity} server slots";

    private Task<ScheduledPlanChange?> GetPendingChangeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Set<ScheduledPlanChange>()
            .Include(spc => spc.Plan).ThenInclude(p => p.Prices)
            .FirstOrDefaultAsync(
                spc => spc.TenantId == tenantId && spc.AppliedOn == null && spc.CancelledOn == null,
                cancellationToken);

    /// <summary>
    /// How many servers this tenant currently owns. Names the tenant explicitly rather than relying on
    /// the ambient one, because this also runs from the background applier where there isn't one -
    /// soft-deleted servers stay excluded automatically.
    /// </summary>
    private Task<int> CountServersAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .CountAsync(s => s.TenantId == tenantId, cancellationToken);

    // Delegated to BillingTerms rather than kept local, so a term reads the same way here, in the Panel,
    // and anywhere else it's shown - there's now no fixed set of three to switch over.
    private static string Describe(int termMonths) => BillingTerms.Describe(termMonths);

    private static string Money(decimal amount) => amount.ToString("C", CultureInfo.GetCultureInfo("en-US"));

    private static string Date(DateTimeOffset value) => value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
}
