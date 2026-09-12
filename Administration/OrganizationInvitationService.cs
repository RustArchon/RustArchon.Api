// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using JumpStart.MultiTenant.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IOrganizationInvitationService" />
public class OrganizationInvitationService(
    ApiDbContext dbContext,
    ITenantInvitationService invitations,
    PermissionResolver resolver,
    IPermissionEvaluator permissions,
    ICommunicationPublisher communicationPublisher,
    IConfiguration configuration,
    ILogger<OrganizationInvitationService> logger) : IOrganizationInvitationService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganizationInvitationDto>> ListAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var pending = await invitations.PendingAsync(tenantId, cancellationToken);

        // One lookup for the names rather than one per row. Roles are ITenantScopedOptional, so the
        // ambient filter would answer with this tenant's rows plus the global ones - which is right
        // here, but AcrossAllTenants says so rather than relying on it.
        var roleIds = pending.Where(i => i.RoleId is not null).Select(i => i.RoleId!.Value).ToList();

        var roleNames = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => roleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);

        return [.. pending.Select(i => new OrganizationInvitationDto
        {
            Id = i.Id,
            Email = i.Email,
            RoleId = i.RoleId,
            RoleName = i.RoleId is { } roleId ? roleNames.GetValueOrDefault(roleId) : null,
            InvitedOn = i.CreatedOn,
            ExpiresOn = i.ExpiresOn
        })];
    }

    /// <inheritdoc />
    public async Task<OrganizationInvitationDto> InviteAsync(
        Guid tenantId, Guid inviterUserId, string email, Guid? roleId,
        CancellationToken cancellationToken = default)
    {
        var address = (email ?? string.Empty).Trim();

        if (address.Length == 0)
        {
            throw new MemberManagementException("An email address is required.");
        }

        // Deliberately shallow. The real test of an address is whether the mail arrives, and a
        // stricter pattern rejects valid addresses more often than it catches typos.
        if (!address.Contains('@', StringComparison.Ordinal) || address.Contains(' ', StringComparison.Ordinal))
        {
            throw new MemberManagementException($"'{address}' doesn't look like an email address.");
        }

        string? roleName = null;

        if (roleId is { } requested)
        {
            var role = await GrantableRoles.FindAsync(dbContext, tenantId, requested, cancellationToken)
                ?? throw new MemberManagementException("That role is not one this organization can grant.");

            await EnsureInviterHoldsEverythingInAsync(role, cancellationToken);
            roleName = role.Name;
        }

        var invitation = await invitations.InviteAsync(
            tenantId, address, roleId, inviterUserId, cancellationToken: cancellationToken);

        await SendInvitationEmailAsync(tenantId, invitation.Email, invitation.Token, cancellationToken);

        logger.LogInformation(
            "Organization {TenantId} invited {Email}{WithRole}.",
            tenantId, invitation.Email, roleName is null ? string.Empty : $" as {roleName}");

        return new OrganizationInvitationDto
        {
            Id = invitation.Id,
            Email = invitation.Email,
            RoleId = invitation.RoleId,
            RoleName = roleName,
            InvitedOn = invitation.CreatedOn,
            ExpiresOn = invitation.ExpiresOn
        };
    }

    /// <inheritdoc />
    public async Task RevokeAsync(
        Guid tenantId, Guid invitationId, Guid revokedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await invitations.RevokeAsync(tenantId, invitationId, revokedByUserId, cancellationToken))
        {
            throw new MemberManagementException("That invitation is no longer outstanding.");
        }
    }

    /// <inheritdoc />
    public async Task<InvitationPreviewDto?> PeekAsync(
        string token, CancellationToken cancellationToken = default)
    {
        var preview = await invitations.PeekAsync(token, cancellationToken);

        return preview is null
            ? null
            : new InvitationPreviewDto
            {
                OrganizationName = preview.TenantName,
                Email = preview.Email,
                ExpiresOn = preview.ExpiresOn,
                IsPending = preview.IsPending
            };
    }

    /// <inheritdoc />
    public async Task<AcceptInvitationResultDto> AcceptAsync(
        string token, Guid userId, string userEmail, CancellationToken cancellationToken = default)
    {
        var result = await invitations.RedeemAsync(token, userId, userEmail, cancellationToken);

        var organization = result.TenantName ?? "that organization";

        // One sentence per outcome. Collapsing these into "invalid link" is what makes somebody give
        // up rather than sign in with their other address, or ask for a fresh invitation.
        var message = result.Outcome switch
        {
            InvitationRedemption.Accepted => $"You've joined {organization}.",
            InvitationRedemption.AlreadyMember => $"You already belong to {organization}.",
            InvitationRedemption.AlreadyUsed => "This invitation has already been used.",
            InvitationRedemption.Expired => "This invitation has expired. Ask them to send a new one.",
            InvitationRedemption.Revoked => "This invitation was withdrawn.",
            InvitationRedemption.WrongRecipient =>
                "This invitation was sent to a different email address. Sign in with the address it "
                + "was sent to, or ask for one addressed to this account.",
            _ => "This invitation link isn't valid."
        };

        return new AcceptInvitationResultDto
        {
            Joined = result.IsMember,
            OrganizationName = result.TenantName ?? string.Empty,
            TenantId = result.IsMember ? result.TenantId : null,
            Message = message
        };
    }

    /// <summary>
    /// Rule 4, applied to an invitation: you cannot arrange for somebody else to receive a permission
    /// you do not hold.
    /// </summary>
    /// <remarks>
    /// The same rule <c>IRoleRepository.AssignUserToRoleAsync</c> applies when a role is handed over
    /// directly, moved to the moment the inviter is actually present. Without it, attaching a role to
    /// an invitation would be a way around the check rather than another route to it - the assignment
    /// itself happens later, unattended, with nobody to measure.
    /// </remarks>
    private async Task EnsureInviterHoldsEverythingInAsync(Role role, CancellationToken cancellationToken)
    {
        var granted = await resolver.ResolveRolePermissionsAsync(role.Id, cancellationToken);
        var held = await permissions.CurrentAsync(cancellationToken);

        var missing = granted.Except(held, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

        if (missing.Count > 0)
        {
            throw new MemberManagementException(
                $"You can't invite somebody as '{role.Name}' because it grants "
                + $"{string.Join(", ", missing)}, which you don't hold yourself.");
        }
    }

    /// <summary>
    /// Queues the invitation email - through <see cref="ICommunicationPublisher"/>'s templated
    /// overload, not built inline, so an admin can change the wording from
    /// <c>Admin/EmailTemplates</c> without a deployment, and so it still leaves the same permanent,
    /// admin-visible <c>Communication</c> record every other outbound email does. Organization-level
    /// (<see cref="Communication.TenantId"/> set) but not attached to any member
    /// (<see cref="Communication.UserId"/> null) - an invitation is addressed to an email address
    /// nobody has necessarily registered yet, so there is no account to tie it to.
    /// </summary>
    private async Task SendInvitationEmailAsync(
        Guid tenantId, string email, string token, CancellationToken cancellationToken)
    {
        var organization = await dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "an organization";

        var panelUrl = (configuration["CorsSettings:BlazorServerUrl"] ?? "https://localhost:7199")
            .TrimEnd('/');

        var link = $"{panelUrl}/Organization/Invitations/Accept?token={WebUtility.UrlEncode(token)}";

        await communicationPublisher.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.OrganizationInvitation,
            new Dictionary<string, string>
            {
                ["OrganizationName"] = organization,
                ["InviteLink"] = link
            },
            email, userId: null, tenantId, cancellationToken: cancellationToken);
    }
}
