// Copyright ©2026 Scott Blomfield

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Anonymous endpoint for what the marketing site's contact form needs to render itself - which
/// captcha widget (if any) to load, and the active queue list.
/// </summary>
/// <remarks>
/// Same <c>[AllowAnonymous]</c>-on-its-own-controller pattern as <see cref="PublicBrandingController"/>:
/// exposes only the two non-secret settings a form needs, never the whole platform settings table.
/// </remarks>
[ApiController]
[Route("api/public/ticketing")]
[AllowAnonymous]
public class PublicTicketingConfigController(
    IPlatformSettingsCache settingsCache, IQueueRepository queues) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PublicTicketingConfigDto>> Get(CancellationToken cancellationToken)
    {
        var captchaProvider = await settingsCache.GetStringAsync(PlatformSettingsRegistry.CaptchaProvider);
        var captchaSiteKey = await settingsCache.GetStringAsync(PlatformSettingsRegistry.CaptchaSiteKey);
        var activeQueues = await queues.GetActiveAsync(cancellationToken);

        return Ok(new PublicTicketingConfigDto
        {
            CaptchaProvider = string.IsNullOrWhiteSpace(captchaProvider)
                ? PlatformSettingsRegistry.CaptchaProviders.None
                : captchaProvider,
            CaptchaSiteKey = captchaSiteKey,
            Queues = [.. activeQueues.Select(q => new QueueDto
            {
                Id = q.Id,
                Name = q.Name,
                Slug = q.Slug,
                Description = q.Description,
                IsDefault = q.IsDefault
            })]
        });
    }
}
