// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin management of global settings: listing and editing their values. See
/// <see cref="PlatformSettingsRegistry"/> for where settings are declared and seeded.
/// </summary>
/// <remarks>
/// <para>
/// Gated by <c>ManagePlatformSettings</c> - a real <c>Permission</c> claim
/// (<see cref="SiteAdminRoleSeeder.ManageSettingsPermission"/>) held by the same global "Site Admin"
/// role <see cref="InvitationCodesController"/>'s <c>PlatformAdmin</c> policy checks a sibling
/// permission from - see <see cref="SiteAdminRoleSeeder"/>.
/// </para>
/// <para>
/// Deliberately no <c>Create</c>/<c>Delete</c> actions - settings are seeded by
/// <see cref="PlatformSettingsRegistry"/>, never invented ad hoc through this UI. Letting an admin
/// type an arbitrary new key here would mean a typo silently creates a dead, unread setting instead of
/// failing loudly; the registry is the only place a new key is ever introduced, in code, next to
/// whatever actually reads it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/platform-settings")]
[Authorize(Policy = "ManagePlatformSettings")]
public class PlatformSettingsController : ControllerBase
{
    private readonly IPlatformSettingRepository _repository;
    private readonly IPlatformSettingsCache _cache;
    private readonly IAppGenerationCache _appGeneration;
    private readonly IApiKeyProtector _apiKeyProtector;
    private readonly IRequestClient<SendTestEmail> _sendTestEmailClient;
    private readonly IMapper _mapper;

    public PlatformSettingsController(
        IPlatformSettingRepository repository,
        IPlatformSettingsCache cache,
        IAppGenerationCache appGeneration,
        IApiKeyProtector apiKeyProtector,
        IRequestClient<SendTestEmail> sendTestEmailClient,
        IMapper mapper)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _appGeneration = appGeneration ?? throw new ArgumentNullException(nameof(appGeneration));
        _apiKeyProtector = apiKeyProtector ?? throw new ArgumentNullException(nameof(apiKeyProtector));
        _sendTestEmailClient = sendTestEmailClient ?? throw new ArgumentNullException(nameof(sendTestEmailClient));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    /// <summary>
    /// Which <see cref="IApiKeyProtector"/> purpose protects a given <see cref="PlatformSettingValueType.Secret"/>
    /// setting's value - one per key, so encrypting one secret setting can never be used to decrypt
    /// another. Extend this alongside <see cref="PlatformSettingsRegistry"/> whenever a new Secret
    /// setting is registered.
    /// </summary>
    private static string SecretPurposeFor(string key) => key switch
    {
        PlatformSettingsRegistry.EmailSmtpPassword => ApiKeyProtectorPurposes.EmailSmtpPassword,
        PlatformSettingsRegistry.EmailApiKey => ApiKeyProtectorPurposes.EmailApiKey,
        _ => throw new InvalidOperationException(
            $"'{key}' is declared as a Secret setting but has no IApiKeyProtector purpose registered.")
    };

    /// <summary>
    /// Maps one entity to its DTO, blanking out a <see cref="PlatformSettingValueType.Secret"/>
    /// setting's actual value - see <see cref="PlatformSettingDto.HasValue"/>'s remarks for why nothing
    /// downstream of this ever sees the real value (encrypted or not) once it leaves the database.
    /// </summary>
    private PlatformSettingDto ToDto(PlatformSetting entity)
    {
        var dto = _mapper.Map<PlatformSettingDto>(entity);

        if (entity.ValueType == Data.PlatformSettingValueType.Secret)
        {
            dto.HasValue = !string.IsNullOrEmpty(entity.Value);
            dto.Value = string.Empty;
        }

        return dto;
    }

    /// <summary>
    /// Lists every platform setting. Unpaginated - this is an admin-only list expected to stay small
    /// (tens of entries, not thousands), so the admin page can render the whole thing at once.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<PlatformSettingDto>>> GetAll()
    {
        var settings = await _repository.GetAllAsync();
        return Ok(settings.OrderBy(s => s.DisplayName).Select(ToDto).ToList());
    }

    /// <summary>
    /// Updates one setting's value, identified by its <see cref="PlatformSetting.Key"/> rather than
    /// its <c>Id</c> - the admin page and every other caller already know the well-known key, never
    /// the row's Guid.
    /// </summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<PlatformSettingDto>> UpdateValue(string key, [FromBody] UpdatePlatformSettingValueDto updateDto)
    {
        var entity = await _repository.GetByKeyAsync(key);
        if (entity is null)
        {
            return NotFound();
        }

        if (entity.ValueType == Data.PlatformSettingValueType.Secret)
        {
            // See PlatformSettingValueType.Secret's remarks - there is no "unchanged" signal an empty
            // submission could mean here, since the admin's browser never had the real value to
            // silently resend in the first place. The Panel's own UI already never calls this for an
            // untouched secret field; a caller that does anyway gets told why, not a silent no-op.
            if (string.IsNullOrEmpty(updateDto.Value))
            {
                return BadRequest("A secret setting can't be set to empty through this endpoint.");
            }

            entity.Value = _apiKeyProtector.Protect(SecretPurposeFor(key), updateDto.Value);
            var savedSecret = await _repository.UpdateAsync(entity);

            // Not written through to the cache - nothing currently reads an email secret via
            // IPlatformSettingsCache (only RustArchon.Worker does, straight from Postgres through the
            // internal endpoint, decrypting on the way), so there's no reason to put ciphertext in
            // Valkey for a value nothing there needs.
            return Ok(ToDto(savedSecret));
        }

        entity.Value = updateDto.Value;
        var updated = await _repository.UpdateAsync(entity);

        // Written through to Valkey immediately, right after Postgres - see IPlatformSettingsCache's
        // remarks for why this is the primary invalidation mechanism, not the cache's own TTL.
        await _cache.SetAsync(key, updateDto.Value);

        // A different concern from the cache write above: that one makes sure the NEXT request sees
        // the new value. This tells every Panel circuit that's already open and rendered the OLD value
        // into its page chrome that its next navigation needs to be a full reload - see
        // PlatformSettingsRegistry.KeysAffectingRenderedChrome's remarks for which keys warrant it.
        if (PlatformSettingsRegistry.KeysAffectingRenderedChrome.Contains(key))
        {
            await _appGeneration.BumpAsync();
        }

        return Ok(ToDto(updated));
    }

    /// <summary>
    /// Sends one test email through whichever provider is currently configured (see
    /// <see cref="PlatformSettingsRegistry.EmailSmtpHost"/>'s remarks for how that's decided) and
    /// reports whether it actually worked - a real send through the same Worker pipeline every
    /// production email goes through, not a separate check that could pass while the real thing fails.
    /// </summary>
    /// <returns>
    /// 200 with the result (which may itself carry <c>Success: false</c> - a rejected send is still an
    /// answer, not a failure of this endpoint); 504 if no Worker instance responded in time, e.g. none
    /// is currently running.
    /// </returns>
    [HttpPost("email/test")]
    public async Task<ActionResult<SendTestEmailResult>> TestEmail(
        [FromBody] SendTestEmailRequestDto request, CancellationToken cancellationToken)
    {
        var siteName = await _cache.GetStringAsync(PlatformSettingsRegistry.SiteName)
            is { Length: > 0 } configuredName
            ? configuredName
            : PlatformSettingsRegistry.DefaultSiteName;

        try
        {
            var response = await _sendTestEmailClient.GetResponse<SendTestEmailResult>(
                new SendTestEmail(
                    request.To,
                    $"{siteName} test email",
                    $"<p>This is a test email from {siteName}'s platform settings page. If you're " +
                        "reading this, your email delivery setup works.</p>"),
                cancellationToken,
                timeout: RequestTimeout.After(s: 20));

            return Ok(response.Message);
        }
        catch (RequestTimeoutException)
        {
            return StatusCode(
                StatusCodes.Status504GatewayTimeout,
                "No RustArchon.Worker instance responded in time - is one running?");
        }
    }
}
