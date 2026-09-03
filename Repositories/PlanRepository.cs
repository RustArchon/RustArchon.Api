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
    public Task<Plan?> GetCheapestActiveAsync() =>
        _dbSet.Where(p => p.Active)
            .OrderBy(p => p.MonthlyPrice)
            .ThenBy(p => p.CreatedOn)
            .FirstOrDefaultAsync();

    /// <inheritdoc />
    public Task<int> GetSubscriberCountAsync(Guid planId) =>
        context.Set<TenantPlan>().CountAsync(tp => tp.PlanId == planId);

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
    public Task<List<Plan>> GetAllOrderedAsync() =>
        _dbSet.OrderBy(p => p.Name).ThenByDescending(p => p.CreatedOn).ToListAsync();
}
