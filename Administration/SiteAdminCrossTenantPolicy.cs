// Copyright ©2026 Scott Blomfield

using System;
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

/// <summary>
/// Lets a site admin act inside a customer's Organization, as an owner of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Supporting a customer means looking at what they are looking at
/// - a server's console, its connection log, why its worker keeps dropping. The alternative to this
/// is a second, read-only copy of every screen, reachable only from the admin console, drifting from
/// the real one from the day it ships. One token that resolves to their Organization makes the
/// application they already have work, unchanged.
/// </para>
/// <para>
/// <strong>What it grants.</strong> Exactly the built-in Owner's permissions in that Organization -
/// no more. A site admin acting as a customer can do what the customer's own owner could do and
/// nothing further, which is the property that makes this support rather than escalation. Notably it
/// does <em>not</em> carry their platform permissions: while acting as a customer they are not
/// administering the platform, and a token that was both would let one screen quietly do the other.
/// </para>
/// <para>
/// <strong>What it costs.</strong> This is real privilege, and it is deliberately not free of
/// consequence: every grant is logged at warning level with who and where. It is gated on
/// <c>Platform.ManageOrganizations</c> - the permission that already lets somebody suspend an
/// Organization, cancel it, disable its servers and send RCON commands to them - so the marginal
/// capability is small even though the reach feels larger.
/// </para>
/// </remarks>
public class SiteAdminCrossTenantPolicy(
    ApiDbContext dbContext,
    PermissionResolver resolver,
    ILogger<SiteAdminCrossTenantPolicy> logger) : ICrossTenantAccessPolicy
{
    /// <inheritdoc />
    public async Task<CrossTenantAccess?> EvaluateAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Resolved with no tenant, which is what makes it the platform-wide question: Site Admin's
        // grants carry no tenant, so this asks "are they an administrator of the platform?" rather
        // than anything about the Organization they are asking to enter.
        var platform = await resolver.ResolveAsync(userId, tenantId: null, cancellationToken);

        if (!platform.Contains(PermissionCatalog.PlatformManageOrganizations))
        {
            return null;
        }

        // The Organization has to exist. Without this a mistyped id would mint a token for a tenant
        // that is not there, and every screen behind it would fail in some less obvious way.
        var organization = await dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (organization is null)
        {
            return null;
        }

        logger.LogWarning(
            "Site admin {UserId} is acting as organization {TenantId} ({OrganizationName}).",
            userId, tenantId, organization);

        return new CrossTenantAccess(PermissionCatalog.OwnerPermissions);
    }
}
