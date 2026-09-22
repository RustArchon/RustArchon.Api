// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The signed-in person's own settings. Nobody else's: the person is whoever the request is signed in as, so there is no id to name and nothing
/// to authorize beyond being signed in.
/// </summary>
[ApiController]
[Route("api/users/me/profile")]
[Authorize]
public class UserProfileController(IUserProfileStore profiles, IUserContext userContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<UserProfileDto>> Get(CancellationToken cancellationToken)
    {
        if (await userContext.GetCurrentUserIdAsync() is not { } userId)
        {
            return Unauthorized();
        }

        var profile = await profiles.FindAsync(userId, cancellationToken);
        return Ok(new UserProfileDto { UserId = userId, PreferredCulture = profile?.PreferredCulture });
    }

    /// <summary>Saves the settings. Replaces them: a setting left out is cleared.</summary>
    [HttpPut]
    public async Task<ActionResult<UserProfileDto>> Put([FromBody] UpdateUserProfileDto settings, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (await userContext.GetCurrentUserIdAsync() is not { } userId)
        {
            return Unauthorized();
        }

        var saved = await profiles.SetPreferredCultureAsync(userId, settings.PreferredCulture, cancellationToken);
        return Ok(new UserProfileDto { UserId = userId, PreferredCulture = saved.PreferredCulture });
    }
}

/// <summary>
/// People's settings for the Panel to keep in step, authenticated by the shared secret rather than a person's sign-in: at sign-up there is no signed-in
/// person yet, and the Panel is the one that knows when someone changes their language. Not published outside the network the Panel is on.
/// </summary>
[ApiController]
[Route("internal/users")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalUserProfilesController(IUserProfileStore profiles) : ControllerBase
{
    [HttpGet("{userId:guid}/profile")]
    public async Task<ActionResult<UserProfileDto>> Get(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await profiles.FindAsync(userId, cancellationToken);
        return Ok(new UserProfileDto { UserId = userId, PreferredCulture = profile?.PreferredCulture });
    }

    [HttpPut("{userId:guid}/profile")]
    public async Task<ActionResult<UserProfileDto>> Put(Guid userId, [FromBody] UpdateUserProfileDto settings, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var saved = await profiles.SetPreferredCultureAsync(userId, settings.PreferredCulture, cancellationToken);
        return Ok(new UserProfileDto { UserId = userId, PreferredCulture = saved.PreferredCulture });
    }

    /// <summary>
    /// Records the languages the identity database already holds, for people who have no profile yet. Never overwrites one that exists, so it is safe to
    /// repeat and safe to run alongside people changing their language. Returns how many profiles it created.
    /// </summary>
    [HttpPost("profiles/backfill")]
    public async Task<ActionResult<int>> Backfill([FromBody] BackfillUserProfilesDto batch, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var created = await profiles.BackfillPreferredCulturesAsync(batch.Items.Select(i => (i.UserId, i.PreferredCulture)).ToList(), cancellationToken);
        return Ok(created);
    }
}
