// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using AutoMapper;
using Correlate;
using JumpStart.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// API controller for managing <see cref="RustServer"/> entities.
/// </summary>
/// <remarks>
/// Inherits standard CRUD (Get/List/Create/Update/Delete) from
/// <see cref="ApiControllerBase{TEntity, TDto, TCreateDto, TUpdateDto, TRepository}"/>, each already
/// protected by <c>[EntityAuthorize]</c> and automatically scoped to the caller's tenant. The RCON
/// password never appears in <see cref="RustServerDto"/> - it is encrypted via
/// <see cref="IRconCredentialProtector"/> on the way in and simply never mapped back out.
/// </remarks>
[Route("api/[controller]")]
[ApiController]
public class RustServersController
    : ApiControllerBase<RustServer, RustServerDto, CreateRustServerDto, UpdateRustServerDto, IRustServerRepository>
{
    private readonly IRconCredentialProtector _rconCredentialProtector;

    public RustServersController(
        IRustServerRepository repository,
        IMapper mapper,
        ILogger<RustServersController> logger,
        ICorrelationContextAccessor correlationContext,
        IRconCredentialProtector rconCredentialProtector)
        : base(repository, mapper, logger, correlationContext)
    {
        _rconCredentialProtector = rconCredentialProtector ?? throw new ArgumentNullException(nameof(rconCredentialProtector));
    }

    /// <summary>
    /// Encrypts the plaintext RCON password mapped onto the entity from <see cref="CreateRustServerDto"/>
    /// before it is ever persisted.
    /// </summary>
    protected override (bool isValid, object? errorResult) OnBeforeCreate(RustServer entity)
    {
        entity.RconPassword = _rconCredentialProtector.Protect(entity.RconPassword);
        return (true, null);
    }

    /// <summary>
    /// Updates an existing server. <see cref="UpdateRustServerDto.RconPassword"/> is optional -
    /// when omitted, the existing encrypted password is left untouched (the mapping profile ignores
    /// it entirely); when supplied, it replaces the stored password after encryption.
    /// </summary>
    [HttpPut("{id}")]
    [JumpStart.Repositories.EntityAuthorize(action: "Update")]
    public override async Task<ActionResult<RustServerDto>> Update(Guid id, [FromBody] UpdateRustServerDto updateDto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

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

        if (!string.IsNullOrEmpty(updateDto.RconPassword))
        {
            entity.RconPassword = _rconCredentialProtector.Protect(updateDto.RconPassword);
        }

        var updated = await _repository.UpdateAsync(entity);
        return Ok(_mapper.Map<RustServerDto>(updated));
    }
}
