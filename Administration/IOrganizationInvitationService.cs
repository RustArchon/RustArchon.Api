// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// Inviting people into an Organization, and letting them in when they accept.
/// </summary>
/// <remarks>
/// <para>
/// The RustArchon half of a feature whose lifecycle lives in JumpStart. That split is deliberate:
/// unguessable single-use tokens, expiry, revocation and binding a redemption to the invited address
/// are the same problem in every multi-tenant application, so they belong in the framework
/// (<c>ITenantInvitationService</c>). What is ours is everything the framework has no business
/// deciding - whether this inviter may attach that role, what the email says, and where the link
/// points.
/// </para>
/// <para>
/// <strong>The role check happens at invite time, not at acceptance.</strong> It has to: the rule
/// being applied is "you cannot hand out a permission you do not hold yourself", and the only moment
/// the inviter is present to be measured is when they issue the invitation. See
/// <see cref="InviteAsync"/>.
/// </para>
/// </remarks>
public interface IOrganizationInvitationService
{
    /// <summary>Outstanding invitations for this Organization.</summary>
    Task<IReadOnlyList<OrganizationInvitationDto>> ListAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invites <paramref name="email"/> to join, optionally arriving with a role, and sends the mail.
    /// </summary>
    /// <exception cref="MemberManagementException">
    /// When the address is missing or malformed, the role is not one this Organization may grant, or
    /// the inviter does not hold everything that role grants.
    /// </exception>
    Task<OrganizationInvitationDto> InviteAsync(
        Guid tenantId, Guid inviterUserId, string email, Guid? roleId,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraws an invitation, so its link stops working.</summary>
    /// <exception cref="MemberManagementException">When no such invitation is outstanding here.</exception>
    Task RevokeAsync(
        Guid tenantId, Guid invitationId, Guid revokedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes an invitation to whoever opened the link, without consuming it.
    /// </summary>
    /// <returns><c>null</c> when the token names nothing.</returns>
    Task<InvitationPreviewDto?> PeekAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an invitation on behalf of the signed-in caller.
    /// </summary>
    /// <param name="userEmail">
    /// The address the caller is authenticated as, taken from their verified identity - never from
    /// anything they supplied. It is the second factor that keeps a forwarded link harmless.
    /// </param>
    Task<AcceptInvitationResultDto> AcceptAsync(
        string token, Guid userId, string userEmail, CancellationToken cancellationToken = default);
}
