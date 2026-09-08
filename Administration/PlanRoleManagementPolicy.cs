// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Administration;

/// <summary>
/// Gates role management on the Organization's plan - the RustArchon half of JumpStart's
/// <see cref="IRoleManagementPolicy"/> seam.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The only place a subscription tier enters authorization.</strong> JumpStart owns the
/// mechanism - the closed registry, the scope rules, "nobody grants what they do not hold" - and asks
/// this one question without ever learning what a plan is. That separation is what keeps the
/// framework reusable: another application answers the same question from a feature flag or a
/// contract term, and neither has to teach JumpStart its vocabulary.
/// </para>
/// <para>
/// This is also what finally makes <see cref="Plan.HasRoles"/> mean something. It has been rendered
/// on the pricing page as "Role separation (Owner, Admin)" while no code read it - the promise was
/// real and the enforcement was not.
/// </para>
/// </remarks>
public class PlanRoleManagementPolicy(
    ApiDbContext dbContext, IPermissionRegistry registry) : IRoleManagementPolicy
{
    /// <inheritdoc />
    public async Task<bool> CanManageRolesAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        // The plan on the Organization's open subscription. A tenant with no open subscription
        // cannot define roles - there is no plan to grant the capability, and SubscriptionBackfiller
        // exists to make that state not happen.
        return await dbContext.Set<Subscription>()
            .Where(s => s.TenantId == tenantId && s.EndDate == null)
            .Select(s => s.Plan.HasRoles)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> GrantablePermissionsAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!await CanManageRolesAsync(tenantId, cancellationToken))
        {
            return [];
        }

        // Everything the registry already marks delegable. RustArchon narrows by tier - whether you
        // may define roles at all - rather than by which permissions those roles may contain; the
        // per-permission decision is made once, in PermissionCatalog, and applies to every tier.
        // Notably Subscription.Manage is not delegable there, so no custom role can ever spend money
        // regardless of plan.
        return registry.All
            .Where(p => p.DelegableByTenantAdmin)
            .Select(p => p.Name)
            .ToList();
    }
}
