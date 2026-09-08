// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// Who belongs to which Organization, what they can do there, and who administers the platform itself.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Half of "who" lives somewhere else.</strong> This side owns membership
/// (<c>UserTenant</c>), role assignment (<c>UserRole</c>) and the platform-wide "Site Admin" role;
/// names, emails and passwords live in the Panel's Identity store, in a different database. So every
/// method here speaks in user ids and none of them can tell you who a user is. The Panel joins the two
/// - it is the only component that can see both - which is why the directory screen lives there and
/// not behind an endpoint here.
/// </para>
/// <para>
/// <strong>Granting site admin is the sharpest thing in this file.</strong> It hands over every
/// platform permission at once, including this one, so the grant is deliberately explicit rather than a
/// side effect of anything else, and the last holder cannot be removed - see
/// <see cref="RevokeSiteAdminAsync"/>.
/// </para>
/// </remarks>
public interface IAccessAdminService
{
    /// <summary>Adds somebody to an Organization, optionally granting a role at the same time.</summary>
    /// <returns><c>false</c> when they already belong to it.</returns>
    Task<bool> AddMemberAsync(
        Guid tenantId, Guid userId, Guid? roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes somebody from an Organization, along with any roles they held there.
    /// </summary>
    /// <remarks>
    /// The role assignments go too. Leaving them behind would mean a user re-added later silently
    /// regains permissions nobody granted them a second time, which is the sort of thing that is only
    /// discovered by an audit.
    /// </remarks>
    Task<bool> RemoveMemberAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends or restores one person's access to an Organization without removing them from it.
    /// </summary>
    Task<bool> SetMemberActiveAsync(
        Guid tenantId, Guid userId, bool active, CancellationToken cancellationToken = default);

    /// <summary>Grants one of the Organization's own roles to one of its members.</summary>
    Task<bool> AssignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Takes one of the Organization's roles back.</summary>
    Task<bool> UnassignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every user the Api knows about - anyone with a membership or a platform role - with their
    /// Organizations and whether they administer the platform.
    /// </summary>
    Task<IReadOnlyList<PlatformUserDto>> GetDirectoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Grants the platform-wide "Site Admin" role.</summary>
    Task<bool> GrantSiteAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the platform-wide "Site Admin" role away.
    /// </summary>
    /// <remarks>
    /// Refuses to remove the last one. A platform with no site admin cannot grant the role back to
    /// anybody - the endpoint that would do it is itself behind the permission that was just removed -
    /// so the recovery is a hand-written database update. Guarding against it is cheaper than
    /// documenting it.
    /// </remarks>
    Task<bool> RevokeSiteAdminAsync(Guid userId, CancellationToken cancellationToken = default);
}
