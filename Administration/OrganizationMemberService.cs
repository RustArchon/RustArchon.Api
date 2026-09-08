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

/// <inheritdoc cref="IOrganizationMemberService" />
public class OrganizationMemberService(
    ApiDbContext dbContext,
    IRoleRepository roles,
    ILogger<OrganizationMemberService> logger) : IOrganizationMemberService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganizationMemberDto>> ListAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var memberships = await dbContext.Set<UserTenant>()
            .Where(ut => ut.TenantId == tenantId)
            .Select(ut => new { ut.UserId, ut.IsActive, ut.CreatedOn })
            .ToListAsync(cancellationToken);

        // AcrossAllTenants because UserRole and Role are ITenantScopedOptional: the ambient filter
        // admits this tenant's rows and the global ones, and the built-in Owner is a global row whose
        // grant carries the tenant. Filtering on ur.TenantId here is what scopes the answer.
        var grants = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.TenantId == tenantId)
            .Select(ur => new { ur.UserId, ur.RoleId, ur.Role.Name })
            .ToListAsync(cancellationToken);

        var byUser = grants
            .GroupBy(g => g.UserId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name, StringComparer.Ordinal).ToList());

        return memberships
            .Select(m =>
            {
                var held = byUser.GetValueOrDefault(m.UserId, []);

                return new OrganizationMemberDto
                {
                    UserId = m.UserId,
                    IsActive = m.IsActive,
                    JoinedOn = m.CreatedOn,
                    Roles = held.Select(h => h.Name).ToList(),
                    RoleIds = held.Select(h => h.RoleId).ToList()
                };
            })
            .OrderBy(m => m.JoinedOn)
            .ToList();
    }

    /// <inheritdoc />
    public async Task AssignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await EnsureMemberAsync(tenantId, userId, cancellationToken);

        var role = await FindGrantableRoleAsync(tenantId, roleId, cancellationToken);

        // Through the repository, not straight into UserRole: handing somebody a role grants them
        // everything in it, so the same rule that governs adding a permission has to govern this.
        // PermissionGrantException surfaces to the customer with JumpStart's own wording.
        await roles.AssignUserToRoleAsync(userId, roleId, tenantId);

        logger.LogInformation(
            "Organization {TenantId} granted role '{RoleName}' to user {UserId}.",
            tenantId, role.Name, userId);
    }

    /// <inheritdoc />
    public async Task UnassignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await EnsureMemberAsync(tenantId, userId, cancellationToken);

        var role = await FindGrantableRoleAsync(tenantId, roleId, cancellationToken);

        if (role.TenantId is null)
        {
            // The built-in Owner. Everything else can be revoked freely.
            await EnsureNotTheLastOwnerAsync(
                tenantId, userId, cancellationToken,
                "Somebody has to own this organization. Give Owner to another member before taking "
                + "it from the last one.");
        }

        await roles.UnassignUserFromRoleAsync(userId, roleId, tenantId);

        logger.LogInformation(
            "Organization {TenantId} revoked role '{RoleName}' from user {UserId}.",
            tenantId, role.Name, userId);
    }

    /// <inheritdoc />
    public async Task SetActiveAsync(
        Guid tenantId, Guid userId, bool active, CancellationToken cancellationToken = default)
    {
        var membership = await EnsureMemberAsync(tenantId, userId, cancellationToken);

        if (!active)
        {
            await EnsureNotTheLastOwnerAsync(
                tenantId, userId, cancellationToken,
                "This is the organization's only active owner. Suspending them would leave nobody "
                + "able to manage it.");
        }

        membership.IsActive = active;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Organization {TenantId} {Action} user {UserId}.",
            tenantId, active ? "restored" : "suspended", userId);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        var membership = await EnsureMemberAsync(tenantId, userId, cancellationToken);

        await EnsureNotTheLastOwnerAsync(
            tenantId, userId, cancellationToken,
            "This is the organization's only owner. Give Owner to another member before removing them.");

        // The role grants go with the membership, for the same reason they do in IAccessAdminService:
        // leaving them would silently restore permissions if the person is ever added back.
        var assignments = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.TenantId == tenantId && ur.UserId == userId)
            .ToListAsync(cancellationToken);

        dbContext.Set<UserRole>().RemoveRange(assignments);
        dbContext.Set<UserTenant>().Remove(membership);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Organization {TenantId} removed user {UserId}, dropping {Count} role grant(s).",
            tenantId, userId, assignments.Count);
    }

    /// <summary>The membership, or a refusal naming the real reason.</summary>
    private async Task<UserTenant> EnsureMemberAsync(
        Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Set<UserTenant>()
            .FirstOrDefaultAsync(ut => ut.TenantId == tenantId && ut.UserId == userId, cancellationToken)
            ?? throw new MemberManagementException("That person does not belong to this organization.");

    /// <summary>A role this Organization may hand out, or a refusal. See <see cref="GrantableRoles"/>.</summary>
    private async Task<Role> FindGrantableRoleAsync(
        Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        await GrantableRoles.FindAsync(dbContext, tenantId, roleId, cancellationToken)
        ?? throw new MemberManagementException("That role is not one this organization can grant.");

    /// <summary>
    /// Refuses when <paramref name="userId"/> is the only member who would still be an active Owner.
    /// </summary>
    private async Task EnsureNotTheLastOwnerAsync(
        Guid tenantId, Guid userId, CancellationToken cancellationToken, string message)
    {
        var ownerRoleId = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (ownerRoleId is not { } owner)
        {
            return;
        }

        var holders = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.TenantId == tenantId && ur.RoleId == owner)
            .Select(ur => ur.UserId)
            .ToListAsync(cancellationToken);

        // A suspended owner cannot sign in, so they do not count as one for this purpose.
        var active = await dbContext.Set<UserTenant>()
            .Where(ut => ut.TenantId == tenantId && ut.IsActive)
            .Select(ut => ut.UserId)
            .ToListAsync(cancellationToken);

        var remaining = holders
            .Intersect(active)
            .Where(id => id != userId)
            .ToList();

        if (remaining.Count == 0 && holders.Contains(userId))
        {
            throw new MemberManagementException(message);
        }
    }
}
