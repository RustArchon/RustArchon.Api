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

    /// <summary>
    /// Discards an Organization that was created moments ago and turned out not to be wanted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compensating half of <see cref="CreateAsync"/>, and unlike most compensation it is
    /// genuinely unavoidable: registration provisions the Organization before redeeming the
    /// invitation code, precisely so that a provisioning failure costs nobody a code - which leaves
    /// the reverse case, where redemption loses a race after the Organization already exists.
    /// Somebody has to clear that up, and leaving it for a site admin to notice is not clearing up.
    /// </para>
    /// <para>
    /// <strong>Soft delete, not a cascade.</strong> The tenant is marked deleted, which the global
    /// filter then hides everywhere, and the founder's membership and role grants are removed so no
    /// dangling row points at an account that is also going. The subscription and its billing period
    /// stay where they are, unreachable - hard-deleting rows across the billing tables to tidy up a
    /// rare race is a far larger risk than the untidiness it fixes.
    /// </para>
    /// <para>
    /// <strong>It refuses if anything has happened.</strong> A server, a second member, or a raised
    /// invoice all mean this is no longer the empty shell it was a second ago, and the caller is
    /// told so rather than the evidence being quietly removed. In practice a new Organization on the
    /// default plan has none of those - a free plan raises no invoice at all.
    /// </para>
    /// </remarks>
    /// <returns><c>false</c> when the Organization is not eligible, or does not exist.</returns>
    Task<bool> TryDiscardAsync(
        Guid tenantId, Guid founderUserId, CancellationToken cancellationToken = default);
}
