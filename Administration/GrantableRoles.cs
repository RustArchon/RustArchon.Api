// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Administration;

/// <summary>
/// Which roles an Organization is allowed to hand out.
/// </summary>
/// <remarks>
/// <para>
/// One definition, shared by everything that hands out a role - assigning one directly, and
/// attaching one to an invitation. The rule is short enough that each caller could restate it, which
/// is exactly the problem: two copies of a security rule drift, and the copy that drifts is the one
/// nobody was looking at.
/// </para>
/// <para>
/// The exclusion that matters is <c>Site Admin</c>, which is global like the built-in Owner. Naming
/// the one global role that <em>is</em> grantable, rather than testing "is it global?", means this
/// does not quietly admit the next global role somebody adds.
/// </para>
/// </remarks>
public static class GrantableRoles
{
    /// <summary>
    /// The role, if this Organization may grant it - its own, or the built-in Owner.
    /// </summary>
    /// <returns><c>null</c> when the role does not exist or is not this Organization's to grant.</returns>
    public static async Task<Role?> FindAsync(
        DbContext dbContext, Guid tenantId, Guid roleId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var role = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);

        if (role is null)
        {
            return null;
        }

        var grantable = role.TenantId == tenantId
            || (role.TenantId is null && role.Name == BuiltInRoleSeeder.OwnerRoleName);

        return grantable ? role : null;
    }
}
