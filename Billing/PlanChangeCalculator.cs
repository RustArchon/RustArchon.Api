// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <summary>
/// Works out what a requested plan/term change actually does: which part of it happens now, which part
/// waits for the end of the current term, what the immediate part costs, and how the current billing
/// period splits to record it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure.</strong> No database, no clock of its own, no persistence - everything it needs is
/// passed in, and it returns a description of what should happen rather than doing it. That's what lets
/// the preview endpoint and the apply endpoint share one code path: previewing a change is literally
/// "compute the outcome and don't commit it", so the two can't drift.
/// </para>
/// <para>
/// <strong>The rule.</strong> Plan, term and quantity are decided independently, and each moves on its
/// own schedule:
/// </para>
/// <list type="bullet">
/// <item>Anything that costs <em>more</em> - a bigger plan, a longer term, more capacity - takes effect
/// immediately and is prorated.</item>
/// <item>Anything that costs <em>less</em> - a smaller plan, a shorter term, less capacity - takes
/// effect at the end of the current billing period. Never a refund: the tenant paid through the end of
/// that period and keeps what they paid for until it runs out.</item>
/// </list>
/// <para>
/// Applying those independently is what makes the mixed cases well-defined. Upgrading the plan while
/// shortening the term moves the plan today and the term at renewal. Downgrading while lengthening the
/// term moves the term today - which pushes renewal further out - and the plan downgrade then lands at
/// the end of that <em>new, longer</em> term. The second is genuinely surprising, and the quote spells
/// it out before the user accepts precisely because of that.
/// </para>
/// <para>
/// <strong>Quantity is not a special case.</strong> It was added as a third dial precisely because it
/// obeys the same money-direction rule as the first two, splits the period the same way, and composes
/// with them without any new arithmetic - buying two extra slots while downgrading the plan charges for
/// the slots today and moves the plan at renewal, exactly as a term/plan mix already did.
/// </para>
/// <para>
/// <strong>Proration.</strong> The amount owed for an immediate change is:
/// </para>
/// <code>
/// owed = (remaining / newTermDays) * (newPrice - oldPrice)
/// </code>
/// <para>
/// where the new term is anchored to the <em>current period's</em> start, so days already elapsed count
/// against it and <c>remaining = newTermDays - elapsed</c>. <c>oldPrice</c> is the current plan's full
/// price at its current term, and <c>newPrice</c> the new plan's at the new term.
/// </para>
/// <para>
/// This is the collapsed form of the rule as originally specified - "elapsed at the old rate, plus
/// remaining at the new rate, less what's already covered". Written out, the credit term is always the
/// old plan's full-period price, so it cancels against the first term and leaves plain proration of the
/// difference. The two agree exactly: monthly $5 switching to quarterly $12.50 on day 10 of a 90-day
/// quarter gives <c>(80/90) * (12.50 - 5) = $6.67</c>, and a same-term Stone $5 to Metal $20 upgrade on
/// day 10 of 31 gives <c>(21/31) * (20 - 5) = $10.16</c>.
/// </para>
/// <para>
/// Collapsing it is not just tidier, it fixes a real error. The expanded form invited crediting back
/// the cash actually billed so far, which is only equal to the old plan's full-period price until the
/// <em>first</em> mid-period change; after that the two diverge and every later change in the same
/// period is mispriced. A second upgrade (Metal $20 to HQM $50, day 20 of 31) owes
/// <c>(11/31) * 30 = $10.65</c>, not the $12.36 that crediting accumulated cash produces.
/// </para>
/// </remarks>
public static class PlanChangeCalculator
{
    /// <summary>
    /// The outcome of a requested change. <see cref="ImmediatePlan"/>/<see cref="ImmediateTerm"/> hold
    /// the state the subscription should be in straight after the request (equal to the current state
    /// when nothing applies now); the scheduled fields describe the deferred remainder, if any; and
    /// <see cref="ClosingSliceAmount"/>/<see cref="NewSliceAmount"/> say how the current billing period
    /// splits to record an immediate change.
    /// </summary>
    /// <param name="ClosingSliceAmount">
    /// What the currently-open <see cref="SubscriptionPeriod"/> should be re-priced to once it's closed at
    /// the change instant, having covered only part of what it was billed for. Derived by reconciliation
    /// rather than its own formula - <c>oldAmount + owed - newSliceAmount</c> - so the period's total
    /// necessarily rises by exactly the amount charged, with no rounding drift and no double-counting of
    /// slices an earlier change already closed.
    /// </param>
    /// <param name="NewSliceAmount">
    /// What the newly-opened slice covers: the remaining days of the period at the new price.
    /// </param>
    public sealed record Outcome(
        bool HasImmediateChange,
        Plan ImmediatePlan,
        int ImmediateTermMonths,
        int ImmediateQuantity,
        decimal AmountDueNow,
        DateTimeOffset PeriodEndAfterChange,
        decimal ClosingSliceAmount,
        decimal NewSliceAmount,
        bool HasScheduledChange,
        Plan? ScheduledPlan,
        int? ScheduledTermMonths,
        int? ScheduledQuantity,
        DateTimeOffset? ScheduledEffectiveDate);

    /// <summary>
    /// Computes the outcome of moving a subscription onto <paramref name="targetPlan"/> at
    /// <paramref name="targetTerm"/>, as of <paramref name="now"/>.
    /// </summary>
    /// <param name="currentPlan">The plan the tenant is on right now.</param>
    /// <param name="currentSlice">
    /// The open <see cref="SubscriptionPeriod"/> - the billing period in force, and the slice of it
    /// currently being billed. Proration measures against its period, not its own span.
    /// </param>
    /// <param name="targetPlan">The Plan being moved to (may be the one they're already on).</param>
    /// <param name="targetTermMonths">The term being moved to (may be the one they're already on).</param>
    /// <param name="targetQuantity">
    /// The capacity being moved to (may be what they already hold). The caller resolves this - see
    /// <c>SubscriptionService</c> for the rule that a plan change lands entitlement at
    /// <c>max(servers, the target's included units)</c>, capped at the target's ceiling.
    /// </param>
    /// <param name="now">The instant to price the change at - injected, never read from the clock.</param>
    public static Outcome Compute(
        Plan currentPlan,
        SubscriptionPeriod currentSlice,
        Plan targetPlan,
        int targetTermMonths,
        int targetQuantity,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentPlan);
        ArgumentNullException.ThrowIfNull(currentSlice);
        ArgumentNullException.ThrowIfNull(targetPlan);

        var currentQuantity = currentSlice.Quantity;

        // Plans are ranked by what each would cost THIS tenant, per month, at the capacity they hold
        // TODAY - deliberately not at the target quantity. Comparing at different quantities would fold
        // the capacity change into the plan comparison and could flip a plan downgrade into an
        // "upgrade" purely because the tenant also bought slots in the same request; each dial has to be
        // judged on its own or the mixed cases stop being well-defined.
        //
        // It used to be a straight MonthlyPrice comparison, which per-unit pricing breaks twice over: a
        // plan need not offer a monthly term at all, and a per-server plan can undercut a flat tier at
        // one server while costing far more at ten. Since the whole immediate-versus-deferred rule is a
        // money-direction rule, ranking by money at the tenant's own quantity is the only basis that
        // stays self-consistent. The trade: "upgrade" stops strictly meaning "more features".
        var currentRate = MonthlyEquivalent(currentPlan, currentSlice.TermMonths, currentQuantity);
        var targetRate = MonthlyEquivalent(targetPlan, targetTermMonths, currentQuantity);
        var planIsUpgrade = targetPlan.Id != currentPlan.Id && targetRate > currentRate;

        // Terms are ranked by length - a longer commitment is bought up front, so it costs more now.
        var termIsLonger = targetTermMonths > currentSlice.TermMonths;

        // Capacity is ranked by count, which is the same thing as by money: UnitAmount is never
        // negative, so more slots never cost less.
        var quantityIsMore = targetQuantity > currentQuantity;

        // Each dimension independently: the more expensive direction lands now, the cheaper one waits.
        var immediatePlan = planIsUpgrade ? targetPlan : currentPlan;
        var immediateTerm = termIsLonger ? targetTermMonths : currentSlice.TermMonths;
        var immediateQuantity = quantityIsMore ? targetQuantity : currentQuantity;

        var hasImmediateChange = immediatePlan.Id != currentPlan.Id
                                 || immediateTerm != currentSlice.TermMonths
                                 || immediateQuantity != currentQuantity;

        // A longer term keeps the period's start and pushes its end out; anything else leaves the period
        // boundaries alone. This is also the date any deferred part waits for, which is why lengthening
        // the term delays a downgrade that arrived in the same request.
        var periodEndAfterChange = hasImmediateChange && immediateTerm != currentSlice.TermMonths
            ? currentSlice.PeriodStart.AddMonths(immediateTerm)
            : currentSlice.PeriodEnd;

        var amountDueNow = 0m;
        var closingSliceAmount = currentSlice.EarnedAmount;
        var newSliceAmount = 0m;

        if (hasImmediateChange)
        {
            var oldPrice = PriceFor(currentPlan, currentSlice.TermMonths, currentQuantity);
            var newPrice = PriceFor(immediatePlan, immediateTerm, immediateQuantity);

            var termDays = (decimal)(periodEndAfterChange - currentSlice.PeriodStart).TotalDays;

            if (termDays <= 0)
            {
                // Degenerate period (mis-seeded dates, or a change landing exactly on the boundary) -
                // charge the difference outright rather than dividing by zero.
                amountDueNow = Round(Math.Max(0m, newPrice - oldPrice));
                newSliceAmount = Round(newPrice);
            }
            else
            {
                // Clamped so a change requested before the period officially starts, or after it should
                // already have rolled over, can't push either weight outside the period.
                var elapsedDays = Math.Clamp((decimal)(now - currentSlice.PeriodStart).TotalDays, 0m, termDays);
                var remainingDays = termDays - elapsedDays;

                // Never negative: a change pricing out below what's already covered is a downgrade, and
                // downgrades earn no refund.
                amountDueNow = Round(Math.Max(0m, remainingDays / termDays * (newPrice - oldPrice)));
                newSliceAmount = Round(remainingDays / termDays * newPrice);
            }

            // Reconciliation, not a second formula - see ClosingSliceAmount's remarks.
            closingSliceAmount = Round(currentSlice.EarnedAmount + amountDueNow - newSliceAmount);
        }

        // Whatever the user asked for that the immediate part didn't already deliver.
        var hasScheduledChange = targetPlan.Id != immediatePlan.Id
                                 || targetTermMonths != immediateTerm
                                 || targetQuantity != immediateQuantity;

        return new Outcome(
            hasImmediateChange,
            immediatePlan,
            immediateTerm,
            immediateQuantity,
            amountDueNow,
            periodEndAfterChange,
            closingSliceAmount,
            newSliceAmount,
            hasScheduledChange,
            hasScheduledChange ? targetPlan : null,
            hasScheduledChange ? targetTermMonths : null,
            hasScheduledChange ? targetQuantity : null,
            hasScheduledChange ? periodEndAfterChange : null);
    }

    /// <summary>
    /// How many server slots a subscription should hold on <paramref name="targetPlan"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three rules, in order. On a <strong>flat tier</strong> capacity isn't sold separately - the unit
    /// amount is zero, so slots would cost nothing and mean nothing - and quantity is pinned to whatever
    /// the price row includes, whatever was asked for. Otherwise a caller that named a quantity gets it;
    /// one that didn't (an ordinary plan or term change, or a brand-new subscription) lands at
    /// <c>max(servers running, the plan's included units)</c>, so nobody loses a running server to a plan
    /// move and nobody keeps paying for slots the new plan already bundles.
    /// </para>
    /// <para>
    /// Then two clamps: never below the included units, which are paid for inside the base amount
    /// whether held or not, and never above the plan's ceiling where it has one.
    /// </para>
    /// <para>
    /// Lives here rather than on the subscription service because every path that opens a billing period
    /// has to agree on it - a plan change, a renewal, a sign-up and the backfill of a planless tenant.
    /// Sign-up and backfill previously each assumed one slot, which was right only by accident: it
    /// matches this rule for every plan bundling exactly one, and silently under-provisions any plan
    /// that bundles more.
    /// </para>
    /// </remarks>
    /// <param name="requested">A caller-chosen quantity, or <c>null</c> to derive one.</param>
    /// <param name="serverCount">Servers the tenant is running - zero for a brand-new subscription.</param>
    public static int ResolveQuantity(Plan targetPlan, int targetTermMonths, int? requested, int serverCount)
    {
        ArgumentNullException.ThrowIfNull(targetPlan);

        var price = targetPlan.Prices.FirstOrDefault(p => p.TermMonths == targetTermMonths);
        var included = price?.IncludedUnits ?? 1;

        if (price is null || price.UnitAmount <= 0m)
        {
            return included;
        }

        var quantity = Math.Max(requested ?? Math.Max(serverCount, included), included);

        return targetPlan.MaximumServers is { } ceiling ? Math.Min(quantity, ceiling) : quantity;
    }

    /// <summary>
    /// The list price of <paramref name="plan"/> for one period of <paramref name="termMonths"/> at
    /// <paramref name="quantity"/> units of capacity.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="Plan.Prices"/> to be loaded. Returns zero when the plan isn't offered on that
    /// term at all rather than throwing: a caller reaching here with an unavailable term has already been
    /// let through a validation gap, and pricing it at zero is a visible bug rather than a 500 in the
    /// middle of a billing run.
    /// </remarks>
    public static decimal PriceFor(Plan plan, int termMonths, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var price = plan.Prices.FirstOrDefault(p => p.TermMonths == termMonths);
        return price?.AmountFor(quantity) ?? 0m;
    }

    /// <summary>
    /// What <paramref name="plan"/> costs per month on <paramref name="termMonths"/> at
    /// <paramref name="quantity"/> units - the footing on which two plans are compared to decide whether
    /// a change is an upgrade. Falls back to the plan's cheapest per-month rate when it isn't offered on
    /// that term, so a comparison against a plan the tenant is moving away from still means something.
    /// </summary>
    public static decimal MonthlyEquivalent(Plan plan, int termMonths, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var price = plan.Prices.FirstOrDefault(p => p.TermMonths == termMonths);
        if (price is not null)
        {
            return price.MonthlyEquivalentFor(quantity);
        }

        return plan.Prices.Count == 0
            ? 0m
            : plan.Prices.Min(p => p.MonthlyEquivalentFor(quantity));
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
