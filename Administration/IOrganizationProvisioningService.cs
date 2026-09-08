// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;

namespace RustArchon.Api.Administration;

/// <summary>Thrown when an Organization may not be created, with a reason for the customer.</summary>
public class OrganizationProvisioningException(string message) : InvalidOperationException(message);

/// <summary>
/// Bringing a new Organization into existence, fully formed.
/// </summary>
/// <remarks>
/// <para>
/// "Fully formed" is the whole job: a tenant, a plan, the first billing period, an invoice for it if
/// anything is owed, the founder's membership, and the founder's Owner role - in that order, because
/// each step depends on the last. Every Organization must have a plan from the moment it exists, so
/// there is no such thing as a half-provisioned one to recover later.
/// </para>
/// <para>
/// Extracted from <c>AccountBootstrapController</c> when a second caller appeared. Sign-up used to be
/// the only way an Organization was created, so the sequence lived in the endpoint; now that somebody
/// can also create an additional one deliberately, one copy of it is the difference between two paths
/// that agree and two that drift.
/// </para>
/// </remarks>
public interface IOrganizationProvisioningService
{
    /// <summary>
    /// Creates an Organization owned by <paramref name="userId"/>.
    /// </summary>
    /// <param name="planId">
    /// The plan to start on, or <c>null</c> for the platform default - which is what sign-up uses.
    /// </param>
    /// <param name="enforceOnePerOwner">
    /// Whether to apply <see cref="Data.Plan.OnePerOwner"/>. False for sign-up, whose Organization is
    /// the one the rule allows; true for any Organization created deliberately afterwards.
    /// </param>
    /// <exception cref="OrganizationProvisioningException">
    /// When the plan does not exist, is not on sale, or is one this founder may only have once.
    /// </exception>
    Task<Tenant> CreateAsync(
        Guid userId,
        string? name,
        Guid? planId = null,
        bool enforceOnePerOwner = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="userId"/> already created a live Organization on <paramref name="planId"/>,
    /// where that plan is marked <see cref="Data.Plan.OnePerOwner"/>.
    /// </summary>
    /// <remarks>
    /// Always <c>false</c> for a plan without the flag, so callers can ask unconditionally. See
    /// <see cref="Data.Plan.OnePerOwner"/> for what is counted and why it is counted that way.
    /// </remarks>
    Task<bool> WouldExceedOnePerOwnerAsync(
        Guid userId, Guid planId, Guid? ignoringTenantId = null,
        CancellationToken cancellationToken = default);
}
