// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Anonymous endpoint for the platform's own display name and public site URL - the two
/// <see cref="PlatformSettingsRegistry"/> settings a page needs before it knows who's signed in, if
/// anyone (a nav bar, or the login page it renders on).
/// </summary>
/// <remarks>
/// Same <c>[AllowAnonymous]</c>-on-its-own-controller pattern as <see cref="PublicPlansController"/>/
/// <see cref="InvitationsController"/>: the rest of platform settings is legitimately admin-only (SMTP
/// passwords live in that same table), so this exposes only these two rather than opening up
/// <see cref="PlatformSettingsController"/> itself to anonymous callers.
/// </remarks>
[ApiController]
[Route("api/public/branding")]
[AllowAnonymous]
public class PublicBrandingController(IPlatformSettingsCache settingsCache) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SiteBrandingDto>> Get()
    {
        var siteName = await settingsCache.GetStringAsync(PlatformSettingsRegistry.SiteName);
        var siteUrl = await settingsCache.GetStringAsync(PlatformSettingsRegistry.SiteUrl);

        return Ok(new SiteBrandingDto
        {
            SiteName = string.IsNullOrWhiteSpace(siteName) ? PlatformSettingsRegistry.DefaultSiteName : siteName,
            SiteUrl = string.IsNullOrWhiteSpace(siteUrl) ? PlatformSettingsRegistry.DefaultSiteUrl : siteUrl
        });
    }
}
