// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Administration;

/// <summary>
/// The rule that a site administrator can do everything, in any organization: every permission-gated endpoint
/// ([EntityAuthorize] or [RequirePermission]) passes for someone who holds <see cref="PermissionCatalog.PlatformManageOrganizations"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it is needed.</strong> A member's token carries their grants in that organization plus their global ones. A
/// site admin who is also a member of an organization with a lesser role (a support person added as "Server Administrator",
/// say) therefore holds Platform.* and only that lesser role's tenant grants, so an owner-only or narrowly delegated
/// capability answered 403 - while the same person, not being a member, would have been let in as an owner through
/// <see cref="SiteAdminCrossTenantPolicy"/>. Being a member must never be a way to have less access.
/// </para>
/// <para>
/// <strong>Where it sits.</strong> ASP.NET Core passes a requirement when any handler for it succeeds, so this stands beside
/// JumpStart's <see cref="EntityPermissionHandler"/> without changing it: JumpStart stays application-agnostic, and "who is
/// an administrator of the platform" (the same test <see cref="SiteAdminCrossTenantPolicy"/> uses) is RustArchon's rule.
/// It never fails a requirement, so it can only widen access for a site admin, never narrow anyone else's.
/// </para>
/// <para>
/// <strong>Limit.</strong> This decides permission, not tenancy: the token is still stamped for one organization and its data
/// is still filtered to that organization. Reaching an organization the site admin is not a member of remains the
/// cross-tenant policy's job (and is logged there).
/// </para>
/// </remarks>
public class SiteAdminAuthorizationHandler : AuthorizationHandler<EntityPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, EntityPermissionRequirement requirement)
    {
        if (context.User.HasClaim("Permission", PermissionCatalog.PlatformManageOrganizations))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
