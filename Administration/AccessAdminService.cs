// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IAccessAdminService" />
public class AccessAdminService(
    ApiDbContext dbContext, ILogger<AccessAdminService> logger) : IAccessAdminService
{
    /// <inheritdoc />
    public async Task<bool> AddMemberAsync(
        Guid tenantId, Guid userId, Guid? roleId, CancellationToken cancellationToken = default)
    {
        var tenantExists = await dbContext.Set<Tenant>()
            .AnyAsync(t => t.Id == tenantId, cancellationToken);

        if (!tenantExists)
        {
            return false;
        }

        var existing = await dbContext.Set<UserTenant>()
            .FirstOrDefaultAsync(ut => ut.TenantId == tenantId && ut.UserId == userId, cancellationToken);

        if (existing is not null)
        {
            // Already a member. Reactivating a suspended membership is the useful reading of "add them
            // again", and is what an admin looking at a greyed-out row and clicking Add would mean.
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Site admin reactivated membership of tenant {TenantId} for user {UserId}.",
                    tenantId, userId);
                return true;
            }

            return false;
        }

        dbContext.Set<UserTenant>().Add(new UserTenant
        {
            TenantId = tenantId,
            UserId = userId,
            IsActive = true,
            CreatedById = userId,
            CreatedOn = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        if (roleId is { } role)
        {
            await AssignRoleAsync(tenantId, userId, role, cancellationToken);
        }

        logger.LogInformation(
            "Site admin added user {UserId} to tenant {TenantId}.", userId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveMemberAsync(
        Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        var membership = await dbContext.Set<UserTenant>()
            .FirstOrDefaultAsync(ut => ut.TenantId == tenantId && ut.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return false;
        }

        // The role grants go with the membership - see IAccessAdminService.
        var assignments = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.TenantId == tenantId && ur.UserId == userId)
            .ToListAsync(cancellationToken);

        dbContext.Set<UserRole>().RemoveRange(assignments);
        dbContext.Set<UserTenant>().Remove(membership);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Site admin removed user {UserId} from tenant {TenantId}, dropping {Count} role grant(s).",
            userId, tenantId, assignments.Count);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetMemberActiveAsync(
        Guid tenantId, Guid userId, bool active, CancellationToken cancellationToken = default)
    {
        var membership = await dbContext.Set<UserTenant>()
            .FirstOrDefaultAsync(ut => ut.TenantId == tenantId && ut.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return false;
        }

        membership.IsActive = active;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Site admin {Action} membership of tenant {TenantId} for user {UserId}.",
            active ? "restored" : "suspended", tenantId, userId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AssignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        // The role has to be one this Organization can hold: its own, or the built-in Owner, whose
        // single definition is global and whose grant carries the tenant instead. Without the check an
        // admin screen could grant a role defined in a different tenant - or Site Admin, the other
        // global role - by passing its id.
        var role = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .FirstOrDefaultAsync(
                r => r.Id == roleId
                    && (r.TenantId == tenantId
                        || (r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName)),
                cancellationToken);

        if (role is null)
        {
            return false;
        }

        var already = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .AnyAsync(
                ur => ur.UserId == userId && ur.RoleId == roleId && ur.TenantId == tenantId,
                cancellationToken);

        if (already)
        {
            return true;
        }

        dbContext.Set<UserRole>().Add(new UserRole
        {
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Site admin granted role {RoleName} in tenant {TenantId} to user {UserId}.",
            role.Name, tenantId, userId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UnassignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        var assignment = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .FirstOrDefaultAsync(
                ur => ur.UserId == userId && ur.RoleId == roleId && ur.TenantId == tenantId,
                cancellationToken);

        if (assignment is null)
        {
            return false;
        }

        dbContext.Set<UserRole>().Remove(assignment);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Site admin revoked role {RoleId} in tenant {TenantId} from user {UserId}.",
            roleId, tenantId, userId);

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlatformUserDto>> GetDirectoryAsync(
        CancellationToken cancellationToken = default)
    {
        var memberships = await dbContext.Set<UserTenant>()
            .Select(ut => new { ut.UserId, ut.TenantId, ut.IsActive, TenantName = ut.Tenant.Name })
            .ToListAsync(cancellationToken);

        var roleGrants = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.TenantId != null)
            .Select(ur => new { ur.UserId, TenantId = ur.TenantId!.Value, ur.Role.Name })
            .ToListAsync(cancellationToken);

        var siteAdminIds = await SiteAdminIdsAsync(cancellationToken);

        var rolesByMembership = roleGrants
            .GroupBy(g => (g.UserId, g.TenantId))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n).ToList());

        var userIds = memberships.Select(m => m.UserId)
            .Concat(siteAdminIds)
            .Distinct()
            .ToList();

        return userIds
            .Select(userId => new PlatformUserDto
            {
                UserId = userId,
                IsSiteAdmin = siteAdminIds.Contains(userId),
                Organizations = memberships
                    .Where(m => m.UserId == userId)
                    .Select(m => new PlatformUserMembershipDto
                    {
                        TenantId = m.TenantId,
                        TenantName = m.TenantName,
                        IsActive = m.IsActive,
                        Roles = rolesByMembership.GetValueOrDefault((userId, m.TenantId), [])
                    })
                    .OrderBy(o => o.TenantName)
                    .ToList()
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> GrantSiteAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var roleId = await SiteAdminRoleIdAsync(cancellationToken);
        if (roleId is not { } role)
        {
            return false;
        }

        var already = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .AnyAsync(ur => ur.UserId == userId && ur.RoleId == role, cancellationToken);

        if (already)
        {
            return true;
        }

        // TenantId null: this role is platform-wide, not held within any one Organization.
        dbContext.Set<UserRole>().Add(new UserRole
        {
            UserId = userId,
            RoleId = role,
            TenantId = null
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Site admin granted the Site Admin role to user {UserId}.", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeSiteAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var roleId = await SiteAdminRoleIdAsync(cancellationToken);
        if (roleId is not { } role)
        {
            return false;
        }

        var holders = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.RoleId == role)
            .ToListAsync(cancellationToken);

        var assignment = holders.FirstOrDefault(ur => ur.UserId == userId);
        if (assignment is null)
        {
            return false;
        }

        if (holders.Count <= 1)
        {
            // See IAccessAdminService: the recovery from zero site admins is a manual database edit.
            throw new InvalidOperationException(
                "This is the only Site Admin. Grant the role to somebody else before removing it from "
                + "the last holder - otherwise nobody can grant it back.");
        }

        dbContext.Set<UserRole>().Remove(assignment);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Site admin revoked the Site Admin role from user {UserId}.", userId);
        return true;
    }

    private Task<Guid?> SiteAdminRoleIdAsync(CancellationToken cancellationToken) =>
        dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId == null && r.Name == SiteAdminRoleSeeder.RoleName)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<HashSet<Guid>> SiteAdminIdsAsync(CancellationToken cancellationToken)
    {
        var roleId = await SiteAdminRoleIdAsync(cancellationToken);
        if (roleId is not { } role)
        {
            return [];
        }

        var ids = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.RoleId == role)
            .Select(ur => ur.UserId)
            .ToListAsync(cancellationToken);

        return [.. ids];
    }
}
