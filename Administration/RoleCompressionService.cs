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

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IRoleCompressionService" />
public class RoleCompressionService(
    ApiDbContext dbContext, ILogger<RoleCompressionService> logger) : IRoleCompressionService
{
    /// <inheritdoc />
    public async Task<RoleCompressionResult> PreviewAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var customRoles = await CustomRoleIdsAsync(tenantId, cancellationToken);

        if (customRoles.Count == 0)
        {
            return new RoleCompressionResult(0, 0);
        }

        var members = await MembersMissingOwnerAsync(tenantId, cancellationToken);

        return new RoleCompressionResult(members.Count, customRoles.Count);
    }

    /// <inheritdoc />
    public async Task<RoleCompressionResult> CompressAsync(
        Guid tenantId, string reason, CancellationToken cancellationToken = default)
    {
        var customRoles = await CustomRoleIdsAsync(tenantId, cancellationToken);

        if (customRoles.Count == 0)
        {
            return new RoleCompressionResult(0, 0);
        }

        var ownerRoleId = await OwnerRoleIdAsync(cancellationToken);
        var promote = await MembersMissingOwnerAsync(tenantId, cancellationToken);

        foreach (var userId in promote)
        {
            // Written directly rather than through IRoleRepository: this is the system acting on a
            // billing event, with no grantor, and the assignment is of the built-in role the
            // Organization is entitled to on any plan. Deliberately not AssignUserToRoleAsSystemAsync
            // only because that would re-validate every Owner permission once per member for no gain.
            dbContext.Set<UserRole>().Add(new UserRole
            {
                UserId = userId,
                RoleId = ownerRoleId,
                TenantId = tenantId,
                CreatedById = Guid.Empty,
                CreatedOn = DateTimeOffset.UtcNow
            });
        }

        // The grants genuinely go. Soft-deleting the roles alone would already stop them granting
        // (resolution joins through Role, ADR-017), but leaving the assignments would show people as
        // holding a role that no longer exists.
        var assignments = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => customRoles.Contains(ur.RoleId))
            .ToListAsync(cancellationToken);

        dbContext.Set<UserRole>().RemoveRange(assignments);

        var now = DateTimeOffset.UtcNow;

        // Soft delete, so an upgrade can offer to restore what they had.
        foreach (var role in await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => customRoles.Contains(r.Id))
            .ToListAsync(cancellationToken))
        {
            role.DeletedOn = now;
            role.DeletedById = Guid.Empty;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Compressed organization {TenantId}: {Promoted} member(s) promoted to Owner, "
            + "{Removed} custom role(s) retired. Reason: {Reason}",
            tenantId, promote.Count, customRoles.Count, reason);

        return new RoleCompressionResult(promote.Count, customRoles.Count);
    }

    /// <summary>The Organization's own roles - never the built-in one, which is not its to remove.</summary>
    private async Task<List<Guid>> CustomRoleIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Members who do not already hold the built-in Owner role in this Organization.
    /// </summary>
    /// <remarks>
    /// Re-derived at call time rather than taken from a stored list, because compression runs weeks
    /// after the customer agreed to it and the membership will have moved on.
    /// </remarks>
    private async Task<List<Guid>> MembersMissingOwnerAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var ownerRoleId = await OwnerRoleIdAsync(cancellationToken);

        var members = await dbContext.Set<UserTenant>()
            .Where(ut => ut.TenantId == tenantId)
            .Select(ut => ut.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var alreadyOwners = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.TenantId == tenantId && ur.RoleId == ownerRoleId)
            .Select(ur => ur.UserId)
            .ToListAsync(cancellationToken);

        return members.Except(alreadyOwners).ToList();
    }

    private async Task<Guid> OwnerRoleIdAsync(CancellationToken cancellationToken)
    {
        var id = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return id == Guid.Empty
            ? throw new InvalidOperationException(
                "The built-in Owner role is missing - check BuiltInRoleSeeder ran at startup.")
            : id;
    }
}
