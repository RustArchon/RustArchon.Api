// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="Subscription"/> entities.
/// </summary>
/// <remarks>
/// Reads only. Writing a plan change touches both this table and <see cref="SubscriptionPeriod"/> under one
/// transaction, in an order the partial unique indexes constrain - that orchestration lives in
/// <see cref="Billing.SubscriptionService"/>, which owns the transaction, rather than being split
/// across a repository method that can only see half of it.
/// </remarks>
public class SubscriptionRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<Subscription>(context, userContext), ISubscriptionRepository
{
    // Prices are pulled in everywhere a Plan is, without exception. A Plan whose Prices collection is
    // empty doesn't fail loudly - it prices at zero, which made every plan look identical in cost and
    // silently turned every upgrade into a downgrade (deferred to renewal, nothing charged). Loading
    // them by default costs one join; forgetting to costs money.

    /// <inheritdoc />
    public Task<Subscription?> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        _dbSet.Include(tp => tp.Plan).ThenInclude(p => p.Prices)
            .FirstOrDefaultAsync(tp => tp.TenantId == tenantId && tp.EndDate == null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Subscription>> GetHistoryForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await _dbSet.Include(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartDate)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Restricted to the <em>open</em> subscription, not merely the tenant's newest period. In normal
    /// operation those are the same row - intervals are contiguous and move forward, so the newest
    /// period always belongs to the open interval - but "newest by StartDate" quietly stops meaning
    /// "current" the moment that assumption is violated, and it then returns a period from a
    /// subscription the tenant has already left. Saying which subscription is meant costs one predicate
    /// and matches how <c>ReportingService.CurrentTermsAsync</c> asks the same question.
    /// </remarks>
    public Task<SubscriptionPeriod?> GetCurrentTermAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        context.Set<SubscriptionPeriod>()
            .Include(t => t.Subscription).ThenInclude(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(t => t.Subscription.TenantId == tenantId && t.Subscription.EndDate == null)
            .OrderByDescending(t => t.StartDate)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubscriptionPeriod>> GetBillingHistoryAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await context.Set<SubscriptionPeriod>()
            .Include(t => t.Subscription).ThenInclude(tp => tp.Plan).ThenInclude(p => p.Prices)
            .Where(t => t.Subscription.TenantId == tenantId)
            .OrderByDescending(t => t.StartDate)
            .ToListAsync(cancellationToken);
}
