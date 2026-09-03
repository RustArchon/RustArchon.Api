// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Ensures a single global (not tenant-owned) <see cref="Role"/> exists for platform-level
/// administration - e.g. managing invitation codes and platform-wide settings - and that it grants
/// every permission in <see cref="Permissions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces what used to be a live <c>RUSTARCHON_ADMIN_EMAIL</c> string comparison
/// (<c>Program.cs</c>'s old <c>PlatformAdmin</c> policy) with JumpStart's actual role/permission
/// system - <see cref="Role"/>/<see cref="RolePermission"/>/<see cref="UserRole"/> - the same
/// mechanism every other authorization check in this app uses. <c>RUSTARCHON_ADMIN_EMAIL</c> still
/// exists, but only as a one-time trigger (see <see cref="Controllers.AccountBootstrapController"/>)
/// for granting this role to a newly-bootstrapped account, not as an ongoing per-request check.
/// </para>
/// <para>
/// <strong>Idempotent</strong>, safe to call on every Api startup (mirrors
/// <see cref="AdminInvitationSeeder"/>) and again per-request from
/// <see cref="Controllers.AccountBootstrapController"/> - both just need the role to exist and hold
/// the right permission, however many times they ask. Bypasses <see cref="IRoleRepository"/> the same
/// way <see cref="AdminInvitationSeeder"/> bypasses <c>IInvitationCodeRepository</c>: there is no
/// authenticated actor at Api startup for the repository's normal audit-field plumbing to attribute
/// this to, so <see cref="Guid.Empty"/> marks these rows as system-seeded instead.
/// </para>
/// </remarks>
public static class SiteAdminRoleSeeder
{
    public const string RoleName = "Site Admin";
    public const string ManageInvitationsPermission = "Platform.ManageInvitations";
    public const string ManageSettingsPermission = "Platform.ManageSettings";
    public const string ManagePlansPermission = "Platform.ManagePlans";

    /// <summary>
    /// Every permission the "Site Admin" role should hold. Adding a new platform-level capability
    /// later is adding its permission string here - never a migration.
    /// </summary>
    private static readonly string[] Permissions = [ManageInvitationsPermission, ManageSettingsPermission, ManagePlansPermission];

    /// <summary>
    /// Ensures the global "Site Admin" role exists and grants every permission in
    /// <see cref="Permissions"/>, creating the role and/or any missing grant.
    /// </summary>
    /// <returns>The role's <see cref="Role.Id"/>, whether it already existed or was just created.</returns>
    public static async Task<Guid> EnsureRoleAsync(ApiDbContext dbContext, ILogger logger)
    {
        var role = await dbContext.Set<Role>()
            .FirstOrDefaultAsync(r => r.TenantId == null && r.Name == RoleName);

        if (role is null)
        {
            role = new Role
            {
                Name = RoleName,
                Description = "Platform-wide administration (invitation codes, site settings, etc.), independent of any one tenant.",
                TenantId = null,
                CreatedById = Guid.Empty,
                CreatedOn = DateTimeOffset.UtcNow
            };
            dbContext.Set<Role>().Add(role);
            await dbContext.SaveChangesAsync();

            logger.LogInformation("Seeded global '{RoleName}' role.", RoleName);
        }

        foreach (var permission in Permissions)
        {
            var hasPermission = await dbContext.Set<RolePermission>()
                .AnyAsync(p => p.RoleId == role.Id && p.Permission == permission);

            if (!hasPermission)
            {
                dbContext.Set<RolePermission>().Add(new RolePermission
                {
                    RoleId = role.Id,
                    Permission = permission,
                    CreatedById = Guid.Empty,
                    CreatedOn = DateTimeOffset.UtcNow
                });
                await dbContext.SaveChangesAsync();

                logger.LogInformation("Granted '{Permission}' to the '{RoleName}' role.", permission, RoleName);
            }
        }

        return role.Id;
    }
}
