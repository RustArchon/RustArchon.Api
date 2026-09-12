// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Administration;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Who belongs where, across the whole platform, and who administers it.
/// </summary>
/// <remarks>
/// <para>
/// Answers in user ids and nothing else - see <see cref="IAccessAdminService"/> for why: identity lives
/// in the Panel's Identity store, in a different database, so this side genuinely does not know anyone's
/// name. The Panel merges the two into the directory a human reads.
/// </para>
/// <para>
/// Behind <c>ManageOrganizations</c>, the same permission as the Organizations console it belongs to.
/// Granting site admin is the sharpest action here, and is guarded in the service rather than by a
/// permission of its own: a separate permission would only ever be held by the same people.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "ManageOrganizations")]
public class PlatformUsersController(IAccessAdminService access) : ControllerBase
{
    /// <summary>Every user the Api knows about, with their Organizations and platform role.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlatformUserDto>>> GetDirectory(
        CancellationToken cancellationToken) =>
        Ok(await access.GetDirectoryAsync(cancellationToken));

    /// <summary>
    /// Whether the caller themselves is a Site Admin - see <see cref="PlatformAdminStatusDto"/>'s
    /// remarks for why reaching this at all is the answer.
    /// </summary>
    [HttpGet("me")]
    public ActionResult<PlatformAdminStatusDto> GetMyStatus() =>
        Ok(new PlatformAdminStatusDto { IsSiteAdmin = true });

    /// <summary>Grants the platform-wide "Site Admin" role.</summary>
    [HttpPost("{userId:guid}/site-admin")]
    public async Task<IActionResult> GrantSiteAdmin(Guid userId, CancellationToken cancellationToken) =>
        await access.GrantSiteAdminAsync(userId, cancellationToken)
            ? NoContent()
            : Problem(
                "The Site Admin role does not exist. It is seeded at startup - check SiteAdminRoleSeeder ran.",
                statusCode: StatusCodes.Status500InternalServerError);

    /// <summary>
    /// Takes the platform-wide "Site Admin" role away. Refused for the last holder - see
    /// <see cref="IAccessAdminService.RevokeSiteAdminAsync"/>.
    /// </summary>
    [HttpDelete("{userId:guid}/site-admin")]
    public async Task<IActionResult> RevokeSiteAdmin(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            return await access.RevokeSiteAdminAsync(userId, cancellationToken) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // The last-admin guard. A 409 with the service's own wording, so the screen can show the
            // reason rather than inventing one.
            return Conflict(ex.Message);
        }
    }
}
