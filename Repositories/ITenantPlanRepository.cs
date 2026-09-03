// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="TenantPlan"/> entities.
/// </summary>
public interface ITenantPlanRepository : IRepository<TenantPlan>
{
    /// <summary>This tenant's current Plan assignment - every Organization has exactly one once
    /// created (see <see cref="Data.TenantPlan"/>'s remarks), so <c>null</c> here means account
    /// bootstrap either hasn't run yet or failed partway through.</summary>
    Task<TenantPlan?> GetForTenantAsync(Guid tenantId);
}
