// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Services;

/// <summary>
/// Whether a newer version of a plan is <b>no worse</b> for one subscriber than the version they are on - the test for whether they may be moved to it
/// without being asked. It is judged on what that organization actually has (its billing term and its capacity), not on the plan in general, so a
/// version that drops an annual price does not hold back the people who pay monthly.
/// </summary>
/// <remarks>
/// The same comparison is what a deliberate price increase will need to see later (Scott, 2026-09-21: the site owner will have to be able to raise what
/// subscribers pay, and how that is communicated is theirs, though a mechanism may be provided): "worse" is answered here, with a reason, and that feature
/// decides what to do with it. Today a worse-off subscriber simply stays where they are.
/// </remarks>
public static class PlanMoveAssessment
{
    public const string PriceHigher = "price_higher";
    public const string TermNotOffered = "term_not_offered";
    public const string PricingModelChanged = "pricing_model_changed";
    public const string FewerServers = "fewer_servers";
    public const string FewerUsers = "fewer_users";
    public const string ShorterRetention = "shorter_retention";
    public const string RolesRemoved = "roles_removed";
    public const string AutomaticUpdatesRemoved = "automatic_updates_removed";
    public const string OverServerLimit = "over_server_limit";
    public const string NotInGoodStanding = "not_in_good_standing";
    public const string RenewalDue = "renewal_due";
    public const string CouldNotMove = "could_not_move";

    /// <summary>The first way the newer version is worse for this subscriber, as a code; <c>null</c> when it is no worse. Both plans need their prices loaded.</summary>
    /// <param name="termMonths">The billing term the subscriber is on.</param>
    /// <param name="quantity">The capacity (server slots) they hold now.</param>
    /// <param name="serverCount">The servers they are running.</param>
    public static string? WhyWorse(Plan current, Plan newer, int termMonths, int quantity, int serverCount)
    {
        if (current.PricingModel != newer.PricingModel)
        {
            return PricingModelChanged;
        }

        if (newer.Prices.All(p => p.TermMonths != termMonths))
        {
            return TermNotOffered;
        }

        // What they hold on the newer version: a flat plan grants its included units outright; a per-unit plan keeps what they bought.
        var newerQuantity = PlanChangeCalculator.ResolveQuantity(newer, termMonths, quantity, serverCount);
        if (PlanChangeCalculator.PriceFor(newer, termMonths, newerQuantity) > PlanChangeCalculator.PriceFor(current, termMonths, quantity))
        {
            return PriceHigher;
        }

        if (newerQuantity < quantity || (newer.MaximumServers is { } newerCeiling && (current.MaximumServers is not { } currentCeiling || newerCeiling < currentCeiling)))
        {
            return FewerServers;
        }

        if (newer.MaximumUsers < current.MaximumUsers)
        {
            return FewerUsers;
        }

        if (newer.RetentionHistory < current.RetentionHistory)
        {
            return ShorterRetention;
        }

        if (current.HasRoles && !newer.HasRoles)
        {
            return RolesRemoved;
        }

        if (current.OffersThirdPartyPluginUpdates && !newer.OffersThirdPartyPluginUpdates)
        {
            return AutomaticUpdatesRemoved;
        }

        // Not a pricing question but a data-integrity one, kept for the same reason the force-move keeps it: capacity below the servers running.
        if (newer.MaximumServers is { } ceiling && ceiling < serverCount)
        {
            return OverServerLimit;
        }

        return null;
    }

    /// <summary>English wording for a code, for a client that does not translate it.</summary>
    public static string Describe(string code) => code switch
    {
        PriceHigher => "the newer version costs more on their billing term",
        TermNotOffered => "the newer version is not offered on their billing term",
        PricingModelChanged => "the newer version is priced differently (flat versus per server)",
        FewerServers => "the newer version allows fewer servers",
        FewerUsers => "the newer version allows fewer users",
        ShorterRetention => "the newer version keeps history for fewer days",
        RolesRemoved => "the newer version has no role separation",
        AutomaticUpdatesRemoved => "the newer version does not include automatic third-party plugin updates",
        OverServerLimit => "they run more servers than the newer version allows",
        NotInGoodStanding => "their subscription is suspended, cancelled or the organization is inactive",
        RenewalDue => "their billing period has ended and is about to renew; try again shortly",
        CouldNotMove => "the move failed for this organization and was undone",
        _ => code
    };
}

/// <summary>Moves the current subscribers of one version of a plan to a newer version, keeping their billing period.</summary>
public interface IPlanSubscriberMover
{
    /// <summary>What a move would do, without doing it. <paramref name="newer"/> need not be saved (it may be the proposed terms of a plan about to be created).</summary>
    Task<PlanMovePreviewDto> PreviewAsync(Plan current, Plan newer, CancellationToken cancellationToken = default);

    /// <summary>Moves every current subscriber of <paramref name="current"/> that the newer version is no worse for. <paramref name="newer"/> must be saved.</summary>
    Task<PlanMoveResultDto> MoveAsync(Plan current, Plan newer, Guid? actingUserId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>What a move is:</b> the organization goes onto the newer version <em>now</em>, and nothing else about its billing changes. Its period, term and renewal
/// date are exactly as they were; nothing is charged and nothing is refunded (Scott, 2026-09-21: keep the current period). Its next renewal is billed at the
/// newer version's price, which the "no worse" test guarantees is not higher.
/// </para>
/// <para>
/// <b>How, in the same shape a mid-period change already takes:</b> the open subscription row is closed and a new one opened on the newer plan, and the
/// current billing slice is split at that instant into two slices of the <em>same</em> period - the elapsed part stays with the old subscription, the
/// remainder goes to the new one, and the amount already earned for the period is divided between them by the time each covers, so the total for the
/// period is unchanged. The history stays truthful: the old interval still says which plan the organization was on and when.
/// </para>
/// <para>
/// Each organization is moved in its own transaction, so one that fails is undone and reported without holding up the rest. Running it again finds
/// nobody left on the old version and moves no one.
/// </para>
/// </remarks>
public class PlanSubscriberMover(ApiDbContext context, TimeProvider clock, ILogger<PlanSubscriberMover> logger) : IPlanSubscriberMover
{
    public async Task<PlanMovePreviewDto> PreviewAsync(Plan current, Plan newer, CancellationToken cancellationToken = default)
    {
        var decisions = await DecideAsync(current, newer, cancellationToken);
        return new PlanMovePreviewDto
        {
            CurrentSubscribers = decisions.Count,
            CanMove = decisions.Count(d => d.StayCode is null),
            WouldStay = Reasons(decisions.Where(d => d.StayCode is not null).Select(d => d.StayCode!))
        };
    }

    public async Task<PlanMoveResultDto> MoveAsync(Plan current, Plan newer, Guid? actingUserId, CancellationToken cancellationToken = default)
    {
        var decisions = await DecideAsync(current, newer, cancellationToken);
        var stayed = decisions.Where(d => d.StayCode is not null).Select(d => d.StayCode!).ToList();
        var moved = 0;

        foreach (var decision in decisions.Where(d => d.StayCode is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryMoveAsync(decision, newer, actingUserId, cancellationToken))
            {
                moved++;
            }
            else
            {
                stayed.Add(PlanMoveAssessment.CouldNotMove);
            }
        }

        logger.LogWarning(
            "Moved {Moved} subscriber(s) of the '{Plan}' plan ({FromPlanId}) to its newer version ({ToPlanId}); {Stayed} stayed. Acting user {UserId}.",
            moved, newer.Name, current.Id, newer.Id, stayed.Count, actingUserId);

        return new PlanMoveResultDto { Moved = moved, Stayed = stayed.Count, StayedBecause = Reasons(stayed) };
    }

    private sealed record Candidate(Subscription Subscription, SubscriptionPeriod? Slice, string? StayCode);

    private async Task<List<Candidate>> DecideAsync(Plan current, Plan newer, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Not tenant-filtered: this is a platform action across every organization on the plan.
        var subscriptions = await context.Set<Subscription>().IgnoreQueryFilters().AsNoTracking()
            .Include(s => s.Tenant)
            .Where(s => s.PlanId == current.Id && s.EndDate == null)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            return [];
        }

        var ids = subscriptions.Select(s => s.Id).ToList();
        var slices = (await context.Set<SubscriptionPeriod>().AsNoTracking().Where(p => ids.Contains(p.SubscriptionId)).ToListAsync(cancellationToken))
            .GroupBy(p => p.SubscriptionId).ToDictionary(g => g.Key, g => g.ToList());

        var tenantIds = subscriptions.Select(s => s.TenantId).ToList();
        var servers = await context.Set<RustServer>().AcrossAllTenants().Where(s => tenantIds.Contains(s.TenantId))
            .GroupBy(s => s.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var result = new List<Candidate>(subscriptions.Count);
        foreach (var subscription in subscriptions)
        {
            var slice = slices.GetValueOrDefault(subscription.Id)?
                .Where(p => p.StartDate <= now && now < p.EndDate).OrderByDescending(p => p.StartDate).FirstOrDefault();

            string? stay = null;
            if (subscription.Status is not (SubscriptionStatus.Active or SubscriptionStatus.PastDue) || !subscription.Tenant.IsActive)
            {
                stay = PlanMoveAssessment.NotInGoodStanding;
            }
            else if (slice is null)
            {
                stay = PlanMoveAssessment.RenewalDue;
            }
            else
            {
                stay = PlanMoveAssessment.WhyWorse(current, newer, slice.TermMonths, slice.Quantity, servers.GetValueOrDefault(subscription.TenantId));
            }

            result.Add(new Candidate(subscription, slice, stay));
        }

        return result;
    }

    private async Task<bool> TryMoveAsync(Candidate candidate, Plan newer, Guid? actingUserId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var tenantId = candidate.Subscription.TenantId;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Loaded afresh and tracked here, inside the transaction: the decision was made on read-only copies, and the tracker is cleared after each
            // organization so one move never leaves state behind for the next.
            var old = await context.Set<Subscription>().IgnoreQueryFilters().FirstAsync(s => s.Id == candidate.Subscription.Id, cancellationToken);
            var slice = await context.Set<SubscriptionPeriod>().FirstAsync(p => p.Id == candidate.Slice!.Id, cancellationToken);
            if (old.EndDate is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;      // it moved (or ended) since the decision was made
            }

            var servers = await context.Set<RustServer>().AcrossAllTenants().CountAsync(s => s.TenantId == old.TenantId, cancellationToken);
            var quantity = PlanChangeCalculator.ResolveQuantity(newer, slice.TermMonths, slice.Quantity, servers);

            // The period is split where it is, in two, and what was earned for it is shared by the time each part covers - nothing charged, nothing refunded.
            var span = slice.EndDate - slice.StartDate;
            var elapsed = Math.Clamp((now - slice.StartDate).TotalSeconds / Math.Max(span.TotalSeconds, 1), 0d, 1d);
            var earnedBefore = Math.Round(slice.EarnedAmount * (decimal)elapsed, 2, MidpointRounding.AwayFromZero);
            var earnedAfter = slice.EarnedAmount - earnedBefore;
            var periodEnd = slice.EndDate;

            slice.EndDate = now;
            slice.EarnedAmount = earnedBefore;
            old.EndDate = now;
            await context.SaveChangesAsync(cancellationToken);

            var opened = new Subscription
            {
                TenantId = old.TenantId,
                PlanId = newer.Id,
                StartDate = now,
                Status = old.Status,
                StatusChangedOn = old.StatusChangedOn,
                StatusReason = old.StatusReason,
                PlanChangeReason = $"Moved to the newer version of the '{newer.Name}' plan when it was superseded.",
                PlanChangedById = actingUserId
            };
            context.Set<Subscription>().Add(opened);
            await context.SaveChangesAsync(cancellationToken);

            context.Set<SubscriptionPeriod>().Add(new SubscriptionPeriod
            {
                SubscriptionId = opened.Id,
                TermMonths = slice.TermMonths,
                Quantity = quantity,
                PeriodStart = slice.PeriodStart,
                PeriodEnd = slice.PeriodEnd,
                StartDate = now,
                EndDate = periodEnd,
                EarnedAmount = earnedAfter
            });
            await context.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            context.ChangeTracker.Clear();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Moving organization {TenantId} to the newer version of '{Plan}' failed and was undone.", tenantId, newer.Name);
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            return false;
        }
    }

    private static List<PlanMoveReasonDto> Reasons(IEnumerable<string> codes) =>
        codes.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new PlanMoveReasonDto { Code = g.Key, Message = PlanMoveAssessment.Describe(g.Key), Count = g.Count() }).ToList();
}
