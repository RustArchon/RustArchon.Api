// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// An Organization managing its own people - who belongs to it and which of its roles they hold.
/// </summary>
/// <remarks>
/// <para>
/// The customer-facing counterpart to <see cref="IAccessAdminService"/>, and the differences are the
/// point. That one is a site admin acting on somebody else's Organization, holds every permission by
/// definition, and writes assignments directly. This one is a member acting inside their own, so the
/// tenant comes from <c>ITenantContext</c> at the controller and every grant goes through
/// <c>IRoleRepository.AssignUserToRoleAsync</c> - which applies JumpStart's rule that you cannot hand
/// somebody a role containing a permission you do not hold yourself. Without that, anyone who could
/// manage members could grant themselves Owner and the permission system would have a hole straight
/// through the middle of it.
/// </para>
/// <para>
/// <strong>An Organization may never be left without an Owner.</strong> Removing the last one would
/// leave nobody able to manage billing, members or roles, and no way back in short of a site admin -
/// so the three operations that could cause it (revoking Owner, suspending a member, removing one)
/// each refuse.
/// </para>
/// <para>
/// Like <see cref="IAccessAdminService"/>, this speaks only in user ids: names and email addresses
/// live in the Panel's Identity store, in a different database. The Panel joins the two.
/// </para>
/// </remarks>
public interface IOrganizationMemberService
{
    /// <summary>Everyone in this Organization, with the roles they hold in it.</summary>
    Task<IReadOnlyList<OrganizationMemberDto>> ListAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants one of the Organization's roles - its own, or the built-in Owner - to one of its members.
    /// </summary>
    /// <exception cref="MemberManagementException">
    /// When the target is not a member, or the role is not one this Organization may grant.
    /// </exception>
    /// <exception cref="JumpStart.Authorization.PermissionGrantException">
    /// When the caller does not hold everything the role grants.
    /// </exception>
    Task AssignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Takes one of the Organization's roles back.</summary>
    /// <exception cref="MemberManagementException">
    /// When this would leave the Organization without an Owner.
    /// </exception>
    Task UnassignRoleAsync(
        Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Suspends or restores one person's access without removing them or their roles.</summary>
    /// <exception cref="MemberManagementException">
    /// When suspending would leave the Organization without an active Owner.
    /// </exception>
    Task SetActiveAsync(
        Guid tenantId, Guid userId, bool active, CancellationToken cancellationToken = default);

    /// <summary>Removes somebody from the Organization, along with the roles they held in it.</summary>
    /// <exception cref="MemberManagementException">
    /// When this would leave the Organization without an Owner.
    /// </exception>
    Task RemoveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a membership request is refused, with a reason for the customer.</summary>
public class MemberManagementException(string message) : InvalidOperationException(message);
