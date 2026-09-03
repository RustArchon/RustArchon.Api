// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="TenantPlan"/> entities.
/// </summary>
public class TenantPlanRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<TenantPlan>(context, userContext), ITenantPlanRepository
{
    /// <inheritdoc />
    public Task<TenantPlan?> GetForTenantAsync(Guid tenantId) =>
        _dbSet.FirstOrDefaultAsync(tp => tp.TenantId == tenantId);
}
