// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="Plan"/> entities.
/// </summary>
public class PlanRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<Plan>(context, userContext), IPlanRepository
{
    /// <inheritdoc />
    public async Task<Plan?> GetCheapestActiveAsync()
    {
        // Ordered in memory rather than in SQL: "cheapest" is now the lowest per-month rate across
        // whichever terms a plan is actually offered on, which is a min over a child collection rather
        // than a column to sort by. The active-plan catalog is a handful of rows, so materialising it
        // costs nothing next to the query that fetched it.
        var active = await _dbSet.Include(p => p.Prices).Where(p => p.Active).ToListAsync();

        return active
            .OrderBy(p => p.Prices.Count == 0 ? decimal.MaxValue : p.Prices.Min(pp => pp.MonthlyEquivalentFor(1)))
            .ThenBy(p => p.CreatedOn)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public Task<int> GetSubscriberCountAsync(Guid planId) =>
        context.Set<Subscription>().CountAsync(tp => tp.PlanId == planId);

    /// <inheritdoc />
    public async Task DeactivateOtherActiveAsync(string name, Guid? excludePlanId)
    {
        var others = await _dbSet
            .Where(p => p.Name == name && p.Active && (excludePlanId == null || p.Id != excludePlanId))
            .ToListAsync();

        foreach (var other in others)
        {
            other.Active = false;
        }

        if (others.Count > 0)
        {
            await context.SaveChangesAsync();
        }
    }

    /// <inheritdoc />
    public Task<Plan?> GetWithPricesAsync(Guid id) =>
        _dbSet.Include(p => p.Prices).FirstOrDefaultAsync(p => p.Id == id);

    /// <inheritdoc />
    public async Task ReplacePricesAsync(Guid planId, IEnumerable<PlanPrice> prices)
    {
        var existing = await context.Set<PlanPrice>().Where(pp => pp.PlanId == planId).ToListAsync();
        context.Set<PlanPrice>().RemoveRange(existing);

        foreach (var price in prices)
        {
            price.PlanId = planId;
            context.Set<PlanPrice>().Add(price);
        }

        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public Task<List<Plan>> GetAllOrderedAsync() =>
        _dbSet.Include(p => p.Prices).OrderBy(p => p.Name).ThenByDescending(p => p.CreatedOn).ToListAsync();
}
