// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
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
    private readonly IMapper _mapper;

    public PlatformSettingsController(
        IPlatformSettingRepository repository, IPlatformSettingsCache cache, IMapper mapper)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    /// <summary>
    /// Lists every platform setting. Unpaginated - this is an admin-only list expected to stay small
    /// (tens of entries, not thousands), so the admin page can render the whole thing at once.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<PlatformSettingDto>>> GetAll()
    {
        var settings = await _repository.GetAllAsync();
        return Ok(_mapper.Map<List<PlatformSettingDto>>(settings.OrderBy(s => s.DisplayName)));
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

        entity.Value = updateDto.Value;
        var updated = await _repository.UpdateAsync(entity);

        // Written through to Valkey immediately, right after Postgres - see IPlatformSettingsCache's
        // remarks for why this is the primary invalidation mechanism, not the cache's own TTL.
        await _cache.SetAsync(key, updateDto.Value);

        return Ok(_mapper.Map<PlatformSettingDto>(updated));
    }
}
