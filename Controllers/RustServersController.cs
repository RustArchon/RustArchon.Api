// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoMapper;
using Correlate;
using JumpStart.Api.Controllers;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
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
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IRequestClient<SendRconCommand> _sendCommandClient;
    private readonly ITenantContext _tenantContext;
    private readonly IRconEventRepository _rconEventRepository;

    public RustServersController(
        IRustServerRepository repository,
        IMapper mapper,
        ILogger<RustServersController> logger,
        ICorrelationContextAccessor correlationContext,
        IRconCredentialProtector rconCredentialProtector,
        IPublishEndpoint publishEndpoint,
        IRequestClient<SendRconCommand> sendCommandClient,
        ITenantContext tenantContext,
        IRconEventRepository rconEventRepository)
        : base(repository, mapper, logger, correlationContext)
    {
        _rconCredentialProtector = rconCredentialProtector ?? throw new ArgumentNullException(nameof(rconCredentialProtector));
        _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        _sendCommandClient = sendCommandClient ?? throw new ArgumentNullException(nameof(sendCommandClient));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _rconEventRepository = rconEventRepository ?? throw new ArgumentNullException(nameof(rconEventRepository));
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
    /// Registers a new server, then publishes <see cref="ConnectToServer"/> so some live
    /// <c>RustArchon.Worker</c> instance claims its connection immediately rather than waiting for
    /// the next claim-sweep interval.
    /// </summary>
    public override async Task<ActionResult<RustServerDto>> Create([FromBody] CreateRustServerDto createDto)
    {
        var result = await base.Create(createDto);

        if (result.Result is CreatedAtActionResult { Value: RustServerDto dto })
        {
            var tenantId = await _tenantContext.GetCurrentTenantIdAsync();
            if (tenantId is { } id)
            {
                await _publishEndpoint.Publish(new ConnectToServer(dto.Id, id));
            }
        }

        return result;
    }

    /// <summary>
    /// Updates an existing server. <see cref="UpdateRustServerDto.RconPassword"/> is optional -
    /// when omitted, the existing encrypted password is left untouched (the mapping profile ignores
    /// it entirely); when supplied, it replaces the stored password after encryption. Publishes
    /// <see cref="ServerLifecycleChanged"/> so whichever worker instance owns this server's
    /// connection (if any) refreshes it with the new details.
    /// </summary>
    // No [EntityAuthorize] here - the base virtual method this overrides already carries
    // [EntityAuthorize(action: "Update")], and EntityPermissionHandler resolves it via
    // GetCustomAttribute<T>() (singular), which throws AmbiguousMatchException the moment both the
    // base method's and an override's copy of the same attribute type are visible via reflection.
    [HttpPut("{id}")]
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

        if (updated.IsEnabled)
        {
            await _publishEndpoint.Publish(new ServerLifecycleChanged(updated.Id, updated.TenantId, ServerLifecycleChangeType.Updated));
        }

        return Ok(_mapper.Map<RustServerDto>(updated));
    }

    /// <summary>
    /// Deletes a server, then publishes <see cref="ServerLifecycleChanged"/> so whichever worker
    /// instance owns its connection (if any) tears it down.
    /// </summary>
    // No [EntityAuthorize] here - see the identical note on the Update override above.
    [HttpDelete("{id}")]
    public override async Task<IActionResult> Delete(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        var result = await base.Delete(id);

        if (result is NoContentResult && entity is not null)
        {
            await _publishEndpoint.Publish(new ServerLifecycleChanged(entity.Id, entity.TenantId, ServerLifecycleChangeType.Deleted));
        }

        return result;
    }

    /// <summary>
    /// Re-enables a previously-disabled server, publishing a fresh <see cref="ConnectToServer"/>
    /// claim so a worker picks it back up.
    /// </summary>
    [HttpPost("{id}/enable")]
    [JumpStart.Repositories.EntityAuthorize(action: "Update")]
    public async Task<IActionResult> Enable(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        if (!entity.IsEnabled)
        {
            entity.IsEnabled = true;
            await _repository.UpdateAsync(entity);
        }

        await _publishEndpoint.Publish(new ConnectToServer(entity.Id, entity.TenantId));
        return NoContent();
    }

    /// <summary>
    /// Disables a server, publishing <see cref="ServerLifecycleChangeType.Disabled"/> so whichever
    /// worker instance owns its connection tears it down. The claim-sweep and any future
    /// <see cref="ConnectToServer"/> claim both skip disabled servers - see
    /// <see cref="IRustServerRepository.GetByIdAcrossTenantsAsync"/>.
    /// </summary>
    [HttpPost("{id}/disable")]
    [JumpStart.Repositories.EntityAuthorize(action: "Update")]
    public async Task<IActionResult> Disable(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        if (entity.IsEnabled)
        {
            entity.IsEnabled = false;
            await _repository.UpdateAsync(entity);
        }

        await _publishEndpoint.Publish(new ServerLifecycleChanged(entity.Id, entity.TenantId, ServerLifecycleChangeType.Disabled));
        return NoContent();
    }

    /// <summary>
    /// Sends a command to a server's live RCON connection, fanning the request out to every
    /// <c>RustArchon.Worker</c> instance - only the one holding the connection actually responds. See
    /// <see cref="SendRconCommand"/>'s remarks for the whole mechanism.
    /// </summary>
    /// <returns>
    /// 200 with the command's result on success; 409 if the owning instance isn't currently connected
    /// (<c>RconCommandResult.Error == "NotConnected"</c>); 504 if no instance responded in time (e.g.
    /// no worker currently owns this server at all).
    /// </returns>
    [HttpPost("{id}/command")]
    [JumpStart.Repositories.EntityAuthorize(action: "Update")]
    public async Task<ActionResult<RconCommandResult>> SendCommand(Guid id, [FromBody] SendCommandRequest request)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        try
        {
            var response = await _sendCommandClient.GetResponse<RconCommandResult>(
                new SendRconCommand(id, request.Command),
                timeout: RequestTimeout.After(s: 10));

            if (!response.Message.Success && response.Message.Error == "NotConnected")
            {
                return Conflict(response.Message);
            }

            return Ok(response.Message);
        }
        catch (RequestTimeoutException)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }

    /// <summary>
    /// Gets this server's captured console/chat history, newest first.
    /// </summary>
    /// <remarks>
    /// A nested action on this controller rather than a separate <c>RconEventsController</c> - a
    /// dedicated controller would need to derive from <see cref="ApiControllerBase{TEntity, TDto,
    /// TCreateDto, TUpdateDto, TRepository}"/> purely so <c>[EntityAuthorize]</c>'s entity-name
    /// resolution works, then block most of its inherited write actions with 405s. Reusing this
    /// controller's existing "Get" permission avoids both the dead endpoints and a new permission to
    /// grant.
    /// </remarks>
    [HttpGet("{id}/events")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PagedResult<RconEventDto>>> GetEvents(
        Guid id, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 100)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        var events = await _rconEventRepository.GetForServerAsync(
            id, new QueryOptions<RconEvent> { PageNumber = pageNumber, PageSize = pageSize });

        return Ok(new PagedResult<RconEventDto>
        {
            Items = _mapper.Map<IEnumerable<RconEventDto>>(events.Items),
            TotalCount = events.TotalCount,
            PageNumber = events.PageNumber,
            PageSize = events.PageSize
        });
    }

    public class SendCommandRequest
    {
        public string Command { get; set; } = string.Empty;
    }
}
