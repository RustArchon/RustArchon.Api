// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// An Organization defining its own roles - the capability the pricing page sells as "role
/// separation".
/// </summary>
/// <remarks>
/// <para>
/// Every method acts on <em>the caller's own</em> Organization: the tenant comes from
/// <c>ITenantContext</c> at the controller, never from the request body, so there is no shape of
/// call that reaches another Organization's roles. That is the opposite of
/// <see cref="IOrganizationAdminService"/>, which deliberately crosses the boundary and is gated by a
/// platform permission.
/// </para>
/// <para>
/// <strong>Three gates, and none of them lives here.</strong> Whether the plan permits roles at all
/// is <c>PlanRoleManagementPolicy</c>; which permissions may go into a role is JumpStart's registry
/// and its four grant rules; whether this caller may manage roles is the
/// <c>Organization.ManageRoles</c> permission on the controller. This service composes them - it does
/// not re-implement any of them, which is what keeps one answer to each question.
/// </para>
/// </remarks>
public interface IOrganizationRoleService
{
    /// <summary>
    /// The Organization's roles - the ones it defined, plus the built-in Owner it always has.
    /// </summary>
    Task<OrganizationRolesDto> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Creates a role for this Organization.</summary>
    /// <exception cref="RoleManagementException">
    /// When the plan does not include role separation, the name is reserved or already in use, or the
    /// name is empty.
    /// </exception>
    Task<OrganizationRoleDto> CreateAsync(
        Guid tenantId, string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>Renames one of the Organization's own roles.</summary>
    Task<OrganizationRoleDto> UpdateAsync(
        Guid tenantId, Guid roleId, string name, string? description,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires one of the Organization's own roles, along with everyone's assignment to it.
    /// </summary>
    /// <remarks>
    /// The role is soft-deleted and the assignments removed. Since ADR-017, soft-deleting alone would
    /// already stop it granting - the assignments go too so nobody is left holding a membership of
    /// something that no longer exists.
    /// </remarks>
    Task DeleteAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the set of permissions a role grants.
    /// </summary>
    /// <remarks>
    /// Every addition goes through JumpStart's grant rules, so a permission that is undeclared,
    /// platform-scoped, not delegable, or not held by the caller is refused - and the whole call is
    /// refused rather than partially applied, because a role that ended up with half the intended
    /// permissions is worse than one that was not changed.
    /// </remarks>
    Task<OrganizationRoleDto> SetPermissionsAsync(
        Guid tenantId, Guid roleId, IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a role-management request is refused, with a reason for the customer.</summary>
public class RoleManagementException(string message) : InvalidOperationException(message);
