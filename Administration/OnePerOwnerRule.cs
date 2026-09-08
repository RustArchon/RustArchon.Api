// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// <see cref="Data.Plan.OnePerOwner"/>, asked the same way from both places that can reach it.
/// </summary>
/// <remarks>
/// <para>
/// Two routes lead to an Organization sitting on a plan: creating it there, and moving it there
/// later. A rule enforced on only the first is not a rule - create on a paid tier, downgrade to the
/// free one, repeat - so both ask this, and they ask the same question rather than two similar ones.
/// </para>
/// <para>
/// The plan carries the flag; nothing here knows which plan is free. See
/// <see cref="Data.Plan.OnePerOwner"/> for what is counted, and for why this is a speed bump rather
/// than a control.
/// </para>
/// </remarks>
public static class OnePerOwnerRule
{
    /// <summary>
    /// Whether <paramref name="founderId"/> already has a live Organization on
    /// <paramref name="planId"/>, where that plan restricts them to one.
    /// </summary>
    /// <param name="ignoringTenantId">
    /// An Organization to leave out of the count - the one being moved, so a plan change that keeps
    /// it where it already is does not refuse itself.
    /// </param>
    public static async Task<bool> WouldExceedAsync(
        DbContext dbContext,
        Guid founderId,
        Guid planId,
        Guid? ignoringTenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var restricted = await dbContext.Set<Plan>()
            .Where(p => p.Id == planId)
            .Select(p => p.OnePerOwner)
            .FirstOrDefaultAsync(cancellationToken);

        if (!restricted)
        {
            return false;
        }

        // Organizations this person founded, still live, currently on this plan. The open interval
        // (EndDate null) is what makes a subscription the current one - see Subscription's remarks.
        var founded = dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.CreatedById == founderId)
            .Select(t => t.Id);

        return await dbContext.Set<Subscription>()
            .AcrossAllTenants()
            .Where(s => s.EndDate == null
                && s.PlanId == planId
                && s.Status != SubscriptionStatus.Cancelled
                && (ignoringTenantId == null || s.TenantId != ignoringTenantId)
                && founded.Contains(s.TenantId))
            .AnyAsync(cancellationToken);
    }

    /// <summary>Who founded an Organization, or <c>null</c> if it has no recorded creator.</summary>
    /// <remarks>
    /// Nullable on purpose. Organizations predating the founder being recorded have
    /// <c>CreatedById</c> of <see cref="Guid.Empty"/>, and treating that as a person would make every
    /// one of them count against a single phantom owner - which would refuse plan changes for
    /// unrelated customers. An unknown founder is simply not subject to the rule.
    /// </remarks>
    public static async Task<Guid?> FounderOfAsync(
        DbContext dbContext, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var founderId = await dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.Id == tenantId)
            .Select(t => t.CreatedById)
            .FirstOrDefaultAsync(cancellationToken);

        return founderId == Guid.Empty ? null : founderId;
    }
}
