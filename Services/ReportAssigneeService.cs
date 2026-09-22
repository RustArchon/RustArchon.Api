// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Services;

/// <summary>
/// Who a report can be assigned to: the active members of the caller's organization who can actually act on a report, meaning they
/// hold <c>RustServer.ManageReports</c>. Assigning to someone who could not change its status would only strand it.
/// </summary>
public interface IReportAssigneeService
{
    /// <summary>The user ids of everyone in the current organization a report can be assigned to. Empty when there is no current organization.</summary>
    Task<IReadOnlyList<Guid>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="userId"/> is one of <see cref="ListAsync"/>'s answers.</summary>
    Task<bool> CanBeAssignedAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// The Api holds no names, so this speaks in user ids and the Panel joins them to its own account store, the same split as
/// <see cref="Administration.IOrganizationMemberService"/>. Fails closed: with no current organization there is nobody to offer, and
/// a suspended member or one whose permission comes from another organization is not offered either.
/// </remarks>
public class ReportAssigneeService(
    ApiDbContext dbContext,
    PermissionResolver resolver,
    ITenantContext tenantContext) : IReportAssigneeService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return [];
        }

        var members = await dbContext.Set<UserTenant>()
            .Where(ut => ut.TenantId == tenantId && ut.IsActive)
            .OrderBy(ut => ut.CreatedOn)
            .Select(ut => ut.UserId)
            .ToListAsync(cancellationToken);

        var assignable = new List<Guid>();
        foreach (var member in members)
        {
            if ((await resolver.ResolveAsync(member, tenantId, cancellationToken)).Contains(PermissionCatalog.ServerManageReports))
            {
                assignable.Add(member);
            }
        }

        return assignable;
    }

    /// <inheritdoc />
    public async Task<bool> CanBeAssignedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return false;
        }

        var isActiveMember = await dbContext.Set<UserTenant>()
            .AnyAsync(ut => ut.TenantId == tenantId && ut.UserId == userId && ut.IsActive, cancellationToken);

        return isActiveMember
            && (await resolver.ResolveAsync(userId, tenantId, cancellationToken)).Contains(PermissionCatalog.ServerManageReports);
    }
}
