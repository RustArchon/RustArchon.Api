// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Administration;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The other end of an invitation - somebody outside an Organization asking to be let in.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="OrganizationInvitationsController"/>, because the caller is
/// in the opposite position. There they are inside a tenant, holding a permission. Here they belong
/// to nothing, hold nothing, and are identified only by the token in their link - so neither a
/// tenant nor a permission can gate these two endpoints, and pretending otherwise would just mean
/// nobody could ever accept.
/// </para>
/// <para>
/// <strong>What stands in for a permission.</strong> Reading an invitation needs the token, which is
/// 256 unguessable bits and reveals only an Organization's name to whoever already has the link.
/// Accepting one needs the token <em>and</em> a signed-in account whose address matches the one it
/// was sent to - so a forwarded or intercepted link cannot put a stranger inside. The email match is
/// taken from the caller's verified identity, never from the request.
/// </para>
/// </remarks>
[ApiController]
[Route("api/invitations/organization")]
public class InvitationAcceptanceController(
    IOrganizationInvitationService invitations) : ControllerBase
{
    /// <summary>
    /// Describes an invitation, so the Panel can say who it is from before asking anybody to sign in.
    /// </summary>
    /// <remarks>
    /// Anonymous on purpose: the person holding the link may not have an account yet, and being told
    /// which Organization is asking for them is the thing that makes them willing to create one. A
    /// token that names nothing returns 404, so a guess and a stale link are indistinguishable.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("{token}")]
    public async Task<ActionResult<InvitationPreviewDto>> Peek(
        string token, CancellationToken cancellationToken)
    {
        var preview = await invitations.PeekAsync(token, cancellationToken);

        return preview is null ? NotFound() : Ok(preview);
    }

    /// <summary>
    /// Accepts an invitation on behalf of the signed-in caller.
    /// </summary>
    /// <remarks>
    /// Always 200, even when the invitation turns out to be spent, expired or addressed to somebody
    /// else. None of those is a server error or a malformed request - they are ordinary things for a
    /// person to click - and the result says which one happened in a sentence meant for a screen.
    /// </remarks>
    [Authorize]
    [HttpPost("{token}/accept")]
    public async Task<ActionResult<AcceptInvitationResultDto>> Accept(
        string token, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return Forbid();
        }

        // The identity the token was issued for. RustArchon's accounts use the email address as the
        // username throughout, so this is the caller's address, established by whoever signed them
        // in rather than asserted by the client.
        var email = User.Identity?.Name;

        if (string.IsNullOrWhiteSpace(email))
        {
            return Forbid();
        }

        return Ok(await invitations.AcceptAsync(token, userId, email, cancellationToken));
    }
}
