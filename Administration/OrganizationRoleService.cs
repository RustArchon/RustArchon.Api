// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IOrganizationRoleService" />
public class OrganizationRoleService(
    ApiDbContext dbContext,
    IRoleRepository roles,
    IPermissionRegistry registry,
    IRoleManagementPolicy policy,
    IPermissionEvaluator permissions,
    ILogger<OrganizationRoleService> logger) : IOrganizationRoleService
{
    /// <inheritdoc />
    public async Task<OrganizationRolesDto> ListAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var planIncludesRoles = await policy.CanManageRolesAsync(tenantId, cancellationToken);
        var grantable = await policy.GrantablePermissionsAsync(tenantId, cancellationToken);

        // Two independent gates - see OrganizationRolesDto.CanEditRoles. The plan decides whether the
        // capability exists for this Organization at all; the permission decides whether this
        // particular member may use it. Reading the list needs neither, only ManageMembers.
        var canEdit = planIncludesRoles
            && await permissions.HasAsync(PermissionCatalog.OrganizationManageRoles, cancellationToken);

        // The built-in Owner is listed alongside the Organization's own roles because that is how a
        // customer thinks about it - it is one of their roles, they simply cannot edit it.
        var visible = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId == tenantId
                || (r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName))
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Description,
                IsBuiltIn = r.TenantId == null,
                Permissions = dbContext.Set<RolePermission>()
                    .Where(rp => rp.RoleId == r.Id)
                    .Select(rp => rp.Permission)
                    .ToList(),
                MemberCount = dbContext.Set<UserRole>()
                    .AcrossAllTenants()
                    .Count(ur => ur.RoleId == r.Id && ur.TenantId == tenantId)
            })
            .ToListAsync(cancellationToken);

        return new OrganizationRolesDto
        {
            PlanIncludesRoles = planIncludesRoles,
            CanEditRoles = canEdit,
            GrantablePermissions = registry.All
                .Where(p => grantable.Contains(p.Name))
                .OrderBy(p => p.Group, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new PermissionOptionDto
                {
                    Name = p.Name,
                    Group = p.Group,
                    Description = p.Description
                })
                .ToList(),
            Roles = visible
                .OrderByDescending(r => r.IsBuiltIn)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new OrganizationRoleDto
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    IsBuiltIn = r.IsBuiltIn,
                    MemberCount = r.MemberCount,
                    Permissions = r.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToList()
                })
                .ToList()
        };
    }

    /// <inheritdoc />
    public async Task<OrganizationRoleDto> CreateAsync(
        Guid tenantId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var trimmed = await ValidateNameAsync(tenantId, name, existingRoleId: null, cancellationToken);

        var role = new Role
        {
            Name = trimmed,
            Description = description?.Trim(),
            TenantId = tenantId
        };

        dbContext.Set<Role>().Add(role);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Organization {TenantId} defined role '{RoleName}'.", tenantId, trimmed);

        return new OrganizationRoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            IsBuiltIn = false
        };
    }

    /// <inheritdoc />
    public async Task<OrganizationRoleDto> UpdateAsync(
        Guid tenantId, Guid roleId, string name, string? description,
        CancellationToken cancellationToken = default)
    {
        var role = await FindOwnRoleAsync(tenantId, roleId, cancellationToken);
        var trimmed = await ValidateNameAsync(tenantId, name, roleId, cancellationToken);

        role.Name = trimmed;
        role.Description = description?.Trim();

        await dbContext.SaveChangesAsync(cancellationToken);

        return new OrganizationRoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            IsBuiltIn = false
        };
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        Guid tenantId, Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await FindOwnRoleAsync(tenantId, roleId, cancellationToken);

        var assignments = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.RoleId == roleId)
            .ToListAsync(cancellationToken);

        dbContext.Set<UserRole>().RemoveRange(assignments);

        // Soft delete: the role stops granting the moment it is deleted, because resolution joins
        // through Role (ADR-017), and the row survives for the audit trail.
        role.DeletedOn = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Organization {TenantId} deleted role '{RoleName}', removing {Count} assignment(s).",
            tenantId, role.Name, assignments.Count);
    }

    /// <inheritdoc />
    public async Task<OrganizationRoleDto> SetPermissionsAsync(
        Guid tenantId, Guid roleId, IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var role = await FindOwnRoleAsync(tenantId, roleId, cancellationToken);
        var wanted = permissions.Distinct(StringComparer.Ordinal).ToList();

        var current = await dbContext.Set<RolePermission>()
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(cancellationToken);

        var currentNames = current.Select(rp => rp.Permission).ToHashSet(StringComparer.Ordinal);

        // Removals first and in their own save, so a refused addition below leaves the role exactly
        // as it was rather than half-stripped. AddPermissionAsync throws on the first bad one.
        var toRemove = current.Where(rp => !wanted.Contains(rp.Permission, StringComparer.Ordinal)).ToList();
        var toAdd = wanted.Where(p => !currentNames.Contains(p)).ToList();

        // Validate every addition before changing anything. The repository would refuse them one at a
        // time anyway, but that would apply the removals and some of the additions first - a role
        // left holding a set nobody asked for is worse than a refused request.
        foreach (var permission in toAdd)
        {
            await EnsureGrantableAsync(tenantId, permission, cancellationToken);
        }

        dbContext.Set<RolePermission>().RemoveRange(toRemove);
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var permission in toAdd)
        {
            // Through the repository, so JumpStart's four rules run - including "the caller holds it
            // themselves", which is the one that cannot be pre-checked from the registry alone.
            await roles.AddPermissionAsync(roleId, permission);
        }

        logger.LogInformation(
            "Organization {TenantId} set role '{RoleName}' to {Count} permission(s).",
            tenantId, role.Name, wanted.Count);

        return new OrganizationRoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            IsBuiltIn = false,
            Permissions = wanted.OrderBy(p => p, StringComparer.Ordinal).ToList()
        };
    }

    /// <summary>
    /// A role belonging to this Organization, refusing the built-in one and anything owned elsewhere.
    /// </summary>
    private async Task<Role> FindOwnRoleAsync(
        Guid tenantId, Guid roleId, CancellationToken cancellationToken)
    {
        // AcrossAllTenants so a role owned by another Organization is found and then *refused* by the
        // check below, rather than silently reported as "no such role" - the two are different, and
        // only one of them tells the caller they asked for something that is not theirs.
        var role = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            ?? throw new RoleManagementException("No such role.");

        if (role.TenantId is null)
        {
            throw new RoleManagementException(
                $"'{role.Name}' is a built-in role and cannot be changed. One definition serves every "
                + "organization, so editing it here would change it for all of them.");
        }

        if (role.TenantId != tenantId)
        {
            throw new RoleManagementException("That role belongs to another organization.");
        }

        return role;
    }

    /// <summary>Validates a role name: present, not reserved, and unused in this Organization.</summary>
    private async Task<string> ValidateNameAsync(
        Guid tenantId, string name, Guid? existingRoleId, CancellationToken cancellationToken)
    {
        if (!await policy.CanManageRolesAsync(tenantId, cancellationToken))
        {
            throw new RoleManagementException(
                "This organization's plan does not include defining its own roles.");
        }

        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new RoleManagementException("A role name is required.");
        }

        if (BuiltInRoleSeeder.ReservedRoleNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            throw new RoleManagementException(
                $"'{trimmed}' is a built-in role name and cannot be reused.");
        }

        var clashes = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .AnyAsync(
                r => r.TenantId == tenantId
                    && r.Name.ToLower() == trimmed.ToLower()
                    && (existingRoleId == null || r.Id != existingRoleId),
                cancellationToken);

        if (clashes)
        {
            throw new RoleManagementException($"This organization already has a role called '{trimmed}'.");
        }

        return trimmed;
    }

    /// <summary>
    /// Pre-checks a permission against the registry and the plan, so a whole request can be refused
    /// before any of it is applied.
    /// </summary>
    private async Task EnsureGrantableAsync(
        Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(permission, out var descriptor))
        {
            throw new RoleManagementException($"'{permission}' is not a permission this system has.");
        }

        if (descriptor.Scope != PermissionScope.Tenant || !descriptor.DelegableByTenantAdmin)
        {
            throw new RoleManagementException(
                $"'{permission}' cannot be granted through an organization's own roles.");
        }

        var grantable = await policy.GrantablePermissionsAsync(tenantId, cancellationToken);

        if (!grantable.Contains(permission))
        {
            throw new RoleManagementException(
                $"'{permission}' is not among the permissions this organization may grant.");
        }
    }
}
