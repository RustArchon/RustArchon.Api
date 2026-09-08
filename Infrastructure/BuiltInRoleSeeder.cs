// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Ensures the built-in <c>Owner</c> role exists once, platform-wide, and moves any per-tenant copy
/// of it onto that single definition.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One definition, granted inside each Organization.</strong> <c>Role.TenantId</c> is null
/// and the grant carries the tenant instead (<c>UserRole.TenantId</c>), which JumpStart's schema has
/// always allowed - <c>Site Admin</c> is already this shape, differing only in that its grants carry
/// no tenant either.
/// </para>
/// <para>
/// It used to be the opposite: <c>AccountBootstrapController</c> minted a separate <c>Owner</c> row
/// per Organization, so Owner's meaning was copied rather than shared. Adding one capability - the
/// <c>RustServer.SendCommand</c> split, say - then meant an insert per tenant forever, and any tenant
/// whose copy was missed diverged silently. Sharing one definition makes that a single row.
/// </para>
/// <para>
/// Grants go through <see cref="IRoleRepository.AddPermissionAsSystemAsync"/> rather than straight
/// into the <c>RolePermission</c> table, so JumpStart's grant rules still apply here: a permission
/// missing from <see cref="PermissionCatalog"/>, or declared platform-scoped by mistake, fails at
/// startup rather than becoming a grant nobody validated. Only the "grantor already holds it" rule is
/// waived, because seeding has no grantor - and it is waived by naming the system method, which is
/// what ADR-019 asks for.
/// </para>
/// </remarks>
public static class BuiltInRoleSeeder
{
    /// <summary>The single built-in Organization role. Reserved - a tenant cannot define its own.</summary>
    public const string OwnerRoleName = "Owner";

    /// <summary>
    /// Role names a tenant may never use for a role of its own.
    /// </summary>
    /// <remarks>
    /// Without this a tenant could create <c>(tenantA, 'Owner')</c>, which does not collide with the
    /// global <c>(NULL, 'Owner')</c> under the unique index, leaving two different things wearing one
    /// name in the same organization.
    /// </remarks>
    public static readonly string[] ReservedRoleNames =
    [
        OwnerRoleName,
        SiteAdminRoleSeeder.RoleName
    ];

    /// <summary>
    /// Creates the global Owner role if missing, tops up its permissions, and retires any
    /// tenant-scoped duplicate.
    /// </summary>
    /// <returns>The global Owner role's id.</returns>
    public static async Task<Guid> EnsureAsync(
        ApiDbContext dbContext, IRoleRepository roles, ILogger logger)
    {
        var owner = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .FirstOrDefaultAsync(r => r.TenantId == null && r.Name == OwnerRoleName);

        if (owner is null)
        {
            owner = new Role
            {
                Name = OwnerRoleName,
                Description =
                    "Full control of an organization - its servers, its billing and its members. "
                    + "Built in; every organization has it and none can edit it.",
                TenantId = null,
                CreatedById = Guid.Empty,
                CreatedOn = DateTimeOffset.UtcNow
            };

            dbContext.Set<Role>().Add(owner);
            await dbContext.SaveChangesAsync();

            logger.LogInformation("Seeded the built-in '{RoleName}' role.", OwnerRoleName);
        }

        // Idempotent top-up: adding a capability to the catalog gives it to every Owner everywhere on
        // the next start, which is the whole point of there being one definition.
        foreach (var permission in PermissionCatalog.OwnerPermissions)
        {
            await roles.AddPermissionAsSystemAsync(owner.Id, permission);
        }

        await MigratePerTenantOwnerRolesAsync(dbContext, owner.Id, logger);

        return owner.Id;
    }

    /// <summary>
    /// Re-points every assignment of a per-tenant <c>Owner</c> role at the global one, then retires
    /// the old role.
    /// </summary>
    /// <remarks>
    /// Idempotent, and safe to leave in place: once no tenant-scoped <c>Owner</c> rows remain it does
    /// nothing. The old role is soft-deleted rather than removed, so the audit trail survives - and
    /// since ADR-017 made permission resolution join through <c>Role</c>, a soft-deleted role
    /// genuinely stops granting. Before that fix this migration would have left every one of its
    /// permissions resolving forever while the role itself vanished from every screen.
    /// </remarks>
    private static async Task MigratePerTenantOwnerRolesAsync(
        ApiDbContext dbContext, Guid globalOwnerRoleId, ILogger logger)
    {
        var perTenant = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId != null && r.Name == OwnerRoleName)
            .Select(r => new { r.Id, r.TenantId })
            .ToListAsync();

        if (perTenant.Count == 0)
        {
            return;
        }

        var oldRoleIds = perTenant.Select(r => r.Id).ToList();

        var assignments = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => oldRoleIds.Contains(ur.RoleId))
            .ToListAsync();

        var moved = 0;

        foreach (var assignment in assignments)
        {
            var alreadyOnGlobal = await dbContext.Set<UserRole>()
                .AcrossAllTenants()
                .AnyAsync(ur => ur.UserId == assignment.UserId
                    && ur.RoleId == globalOwnerRoleId
                    && ur.TenantId == assignment.TenantId);

            if (!alreadyOnGlobal)
            {
                dbContext.Set<UserRole>().Add(new UserRole
                {
                    UserId = assignment.UserId,
                    RoleId = globalOwnerRoleId,
                    TenantId = assignment.TenantId,
                    CreatedById = Guid.Empty,
                    CreatedOn = DateTimeOffset.UtcNow
                });

                moved++;
            }

            // The old assignment goes regardless: leaving it would keep a second, divergent route to
            // the same standing.
            dbContext.Set<UserRole>().Remove(assignment);
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var role in await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => oldRoleIds.Contains(r.Id))
            .ToListAsync())
        {
            role.DeletedOn = now;
            role.DeletedById = Guid.Empty;
        }

        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Migrated {Roles} per-tenant '{RoleName}' role(s) onto the built-in one; {Moved} "
            + "assignment(s) re-pointed.",
            perTenant.Count, OwnerRoleName, moved);
    }
}
