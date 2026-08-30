// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin management of invitation codes: minting, listing, editing, and revoking. See
/// <see cref="InvitationsController"/> for the anonymous side (redemption during registration).
/// </summary>
/// <remarks>
/// Gated by the <c>PlatformAdmin</c> authorization policy (a single admin email read directly from
/// <c>RUSTARCHON_ADMIN_EMAIL</c>, set up in <c>Program.cs</c>), not <c>[EntityAuthorize]</c> -
/// invitation codes aren't tenant-scoped, so there's no tenant-scoped <c>Role</c> that could safely
/// grant access to them (granting it through a tenant's own Owner role would mean the normal sign-up
/// bootstrap flow could accidentally hand every new user admin rights over sign-up itself - a config-
/// only allow-list avoids that). This is a hand-written controller rather than an
/// <see cref="JumpStart.Api.Controllers.ApiControllerBase{TEntity,TDto,TCreateDto,TUpdateDto,TRepository}"/>
/// subclass for the same reason - that base class's actions carry their own per-action
/// <c>[EntityAuthorize]</c> attributes, which would require permission claims nobody will ever hold.
/// </remarks>
[ApiController]
[Route("api/invitation-codes")]
[Authorize(Policy = "PlatformAdmin")]
public class InvitationCodesController : ControllerBase
{
    private readonly IInvitationCodeRepository _repository;
    private readonly IMapper _mapper;

    public InvitationCodesController(IInvitationCodeRepository repository, IMapper mapper)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<InvitationCodeDto>> GetById(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity == null)
        {
            return NotFound();
        }

        return Ok(_mapper.Map<InvitationCodeDto>(entity));
    }

    /// <summary>
    /// Lists invitation codes, newest first by default - mirrors
    /// <see cref="JumpStart.Api.Controllers.ApiControllerBase{TEntity,TDto,TCreateDto,TUpdateDto,TRepository}.GetAll"/>'s
    /// query-parameter shape/validation so the admin page's Refit client can reuse the standard
    /// <c>QueryOptions</c>/<c>PagedResult</c> contract.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<InvitationCodeDto>>> GetAll(
        [FromQuery] int? pageNumber = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDescending = true)
    {
        var options = new QueryOptions<InvitationCode>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            SortDescending = sortDescending
        };

        var sortProperty = sortBy ?? nameof(InvitationCode.CreatedOn);
        var propertyInfo = typeof(InvitationCode).GetProperty(
            sortProperty,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (propertyInfo == null)
        {
            return BadRequest($"Invalid sort property: '{sortProperty}'. Property does not exist on {nameof(InvitationCode)}.");
        }

        var parameter = Expression.Parameter(typeof(InvitationCode), "x");
        var property = Expression.Property(parameter, propertyInfo.Name);
        var conversion = Expression.Convert(property, typeof(object));
        options.SortBy = Expression.Lambda<Func<InvitationCode, object>>(conversion, parameter);

        var result = await _repository.GetAllAsync(options);

        return Ok(new PagedResult<InvitationCodeDto>
        {
            Items = _mapper.Map<List<InvitationCodeDto>>(result.Items),
            TotalCount = result.TotalCount,
            PageNumber = result.PageNumber,
            PageSize = result.PageSize
        });
    }

    /// <summary>
    /// Mints a new invitation code. The code string is always generated server-side via
    /// <see cref="InvitationCodeGenerator"/>, regardless of what (if anything) the client sends.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<InvitationCodeDto>> Create([FromBody] CreateInvitationCodeDto createDto)
    {
        var entity = _mapper.Map<InvitationCode>(createDto);
        entity.Code = InvitationCodeGenerator.Generate();

        if (!string.IsNullOrWhiteSpace(entity.BoundEmail))
        {
            entity.BoundEmail = entity.BoundEmail.Trim().ToLowerInvariant();
        }

        var created = await _repository.AddAsync(entity);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, _mapper.Map<InvitationCodeDto>(created));
    }

    /// <summary>
    /// Edits a code's note or flips <see cref="InvitationCode.IsActive"/> to revoke it before it's
    /// used. <c>BoundEmail</c>/redemption state are untouched - see the mapping profile.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<InvitationCodeDto>> Update(Guid id, [FromBody] UpdateInvitationCodeDto updateDto)
    {
        if (!id.Equals(updateDto.Id))
        {
            return BadRequest("ID mismatch");
        }

        var entity = await _repository.GetByIdAsync(id, null);
        if (entity == null)
        {
            return NotFound();
        }

        _mapper.Map(updateDto, entity);
        var updated = await _repository.UpdateAsync(entity);
        return Ok(_mapper.Map<InvitationCodeDto>(updated));
    }

    /// <summary>
    /// Permanently removes a code that was minted by mistake. Refuses to delete one that's already
    /// been redeemed - <see cref="InvitationCode.RedeemedByEmail"/> is the only record of who used it,
    /// and preserving that is more useful than allowing cleanup here. Use <see cref="Update"/> with
    /// <c>IsActive: false</c> to revoke a code instead.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity == null)
        {
            return NotFound();
        }

        if (entity.RedeemedAtUtc != null)
        {
            return Conflict("This code has already been redeemed and can't be deleted.");
        }

        await _repository.DeleteAsync(id);
        return NoContent();
    }
}
