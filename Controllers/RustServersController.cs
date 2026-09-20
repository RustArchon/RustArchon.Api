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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
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
    private readonly IApiKeyProtector _apiKeyProtector;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IRequestClient<SendRconCommand> _sendCommandClient;
    private readonly ITenantContext _tenantContext;
    private readonly IRconEventRepository _rconEventRepository;
    private readonly IPlayerSessionRepository _playerSessionRepository;
    private readonly IPlayerKillEventRepository _playerKillEventRepository;
    private readonly IServerInfoSnapshotRepository _serverInfoSnapshotRepository;
    private readonly IConnectionLogRepository _connectionLogRepository;
    private readonly IServerPluginRepository _serverPluginRepository;
    private readonly IServerPluginStatusRepository _serverPluginStatusRepository;
    private readonly IPluginScriptService _pluginScriptService;
    private readonly IPluginUpdateService _pluginUpdateService;
    private readonly ISubscriptionRepository _subscriptionRepository;

    public RustServersController(
        IRustServerRepository repository,
        IMapper mapper,
        ILogger<RustServersController> logger,
        ICorrelationContextAccessor correlationContext,
        IRconCredentialProtector rconCredentialProtector,
        IApiKeyProtector apiKeyProtector,
        IPublishEndpoint publishEndpoint,
        IRequestClient<SendRconCommand> sendCommandClient,
        ITenantContext tenantContext,
        IRconEventRepository rconEventRepository,
        IPlayerSessionRepository playerSessionRepository,
        IPlayerKillEventRepository playerKillEventRepository,
        IServerInfoSnapshotRepository serverInfoSnapshotRepository,
        IConnectionLogRepository connectionLogRepository,
        IServerPluginRepository serverPluginRepository,
        IServerPluginStatusRepository serverPluginStatusRepository,
        IPluginScriptService pluginScriptService,
        IPluginUpdateService pluginUpdateService,
        ISubscriptionRepository subscriptionRepository)
        : base(repository, mapper, logger, correlationContext)
    {
        _rconCredentialProtector = rconCredentialProtector ?? throw new ArgumentNullException(nameof(rconCredentialProtector));
        _apiKeyProtector = apiKeyProtector ?? throw new ArgumentNullException(nameof(apiKeyProtector));
        _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        _sendCommandClient = sendCommandClient ?? throw new ArgumentNullException(nameof(sendCommandClient));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _rconEventRepository = rconEventRepository ?? throw new ArgumentNullException(nameof(rconEventRepository));
        _playerSessionRepository = playerSessionRepository ?? throw new ArgumentNullException(nameof(playerSessionRepository));
        _playerKillEventRepository = playerKillEventRepository ?? throw new ArgumentNullException(nameof(playerKillEventRepository));
        _serverInfoSnapshotRepository = serverInfoSnapshotRepository ?? throw new ArgumentNullException(nameof(serverInfoSnapshotRepository));
        _connectionLogRepository = connectionLogRepository ?? throw new ArgumentNullException(nameof(connectionLogRepository));
        _serverPluginRepository = serverPluginRepository ?? throw new ArgumentNullException(nameof(serverPluginRepository));
        _serverPluginStatusRepository = serverPluginStatusRepository ?? throw new ArgumentNullException(nameof(serverPluginStatusRepository));
        _pluginScriptService = pluginScriptService ?? throw new ArgumentNullException(nameof(pluginScriptService));
        _pluginUpdateService = pluginUpdateService ?? throw new ArgumentNullException(nameof(pluginUpdateService));
        _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
    }

    /// <summary>
    /// Encrypts the plaintext RCON password mapped onto the entity from <see cref="CreateRustServerDto"/>
    /// before it is ever persisted.
    /// </summary>
    protected override (bool isValid, object? errorResult) OnBeforeCreate(RustServer entity)
    {
        entity.RconPassword = _rconCredentialProtector.Protect(entity.RconPassword);

        // Both optional (unlike RconPassword) - AutoMapper already copied whatever plaintext arrived
        // (or null) straight across by name, same as it does for RconPassword.
        if (!string.IsNullOrEmpty(entity.SteamApiKey))
        {
            entity.SteamApiKey = _apiKeyProtector.Protect(ApiKeyProtectorPurposes.SteamApiKey, entity.SteamApiKey);
        }

        if (!string.IsNullOrEmpty(entity.GeolocationApiKey))
        {
            entity.GeolocationApiKey = _apiKeyProtector.Protect(ApiKeyProtectorPurposes.GeolocationApiKey, entity.GeolocationApiKey);
        }

        return (true, null);
    }

    /// <summary>
    /// Registers a new server - rejected up front if the tenant has no unused server slot - then
    /// publishes <see cref="ConnectToServer"/> so some live <c>RustArchon.Worker</c> instance claims its
    /// connection immediately rather than waiting for the next claim-sweep interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authoritative check - see <see cref="GetPlanLimit"/> for the same limits exposed as a
    /// read-only, non-blocking heads-up the Panel checks before it even shows the Add Server form.
    /// That endpoint's answer can be stale by the time a user submits (another tab adding a server in
    /// the meantime, say); this one can't, since it and the actual insert run in the same request.
    /// </para>
    /// <para>
    /// <strong>Checked against bought capacity, not against <see cref="Plan.MaximumServers"/>.</strong>
    /// Entitlement is purchased deliberately (see <see cref="SubscriptionPeriod.Quantity"/>) and servers
    /// consume it, so the plan's ceiling is now an upper bound on what may be <em>bought</em> rather than
    /// the limit checked here. On a flat tier the two coincide - quantity is pinned to the plan's
    /// included units - so nothing changes for existing subscribers; on a per-server plan, which has no
    /// ceiling at all, the old check compared against null and let every request through.
    /// </para>
    /// </remarks>
    public override async Task<ActionResult<RustServerDto>> Create([FromBody] CreateRustServerDto createDto)
    {
        var status = await GetPlanLimitStatusAsync();

        // Plan missing entirely fails open (creation proceeds) rather than closed - every
        // Organization is supposed to have exactly one Subscription from the moment it's created (see
        // Subscription's remarks), so hitting this means something upstream is already broken;
        // blocking a legitimate user's request on top of that would make a bad situation worse for
        // no benefit. Same for a subscription with no current billing period.
        if (status.Plan is { } plan && status.Quantity is { } slots && status.CurrentServerCount >= slots)
        {
            return BadRequest(CapacityMessage(plan, slots));
        }

        ActionResult<RustServerDto> result;
        try
        {
            result = await base.Create(createDto);
        }
        catch (DbUpdateException ex) when (IsDuplicateNameViolation(ex))
        {
            // IX_RustServer_TenantId_Name (filtered to live rows - see ApiDbContext.OnModelCreating)
            // rejected this insert. Caught here rather than left to reach the caller as a raw 500: the
            // Panel had no way to tell "a server with that name already exists" apart from any other
            // unhandled failure, and confirmed live, it's the ordinary shape a legitimate duplicate-name
            // attempt takes just as much as the soft-delete-reuse case that motivated the filtered index
            // in the first place.
            return Conflict($"A server named '{createDto.Name}' already exists.");
        }

        if (result.Result is CreatedAtActionResult { Value: RustServerDto dto } && status.TenantId is { } id)
        {
            await _publishEndpoint.Publish(new ConnectToServer(dto.Id, id));
        }

        return result;
    }

    /// <summary>
    /// True if <paramref name="ex"/> is specifically a violation of <c>IX_RustServer_TenantId_Name</c>'s
    /// unique constraint - as opposed to some other <see cref="DbUpdateException"/> (a genuinely bad
    /// request, a connection failure, etc.) that should propagate as a real error instead of being
    /// turned into a 409. Same technique as <c>InvitationCodesController.IsUniqueCodeViolation</c>.
    /// </summary>
    private static bool IsDuplicateNameViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pgEx
        && pgEx.ConstraintName == "IX_RustServer_TenantId_Name";

    /// <summary>
    /// Gets the calling tenant's current Plan limits and how many servers they currently have - lets
    /// the Panel warn "you're at your limit" the moment a user clicks Add Server, before they've
    /// filled out the whole form, instead of only after submitting it and having <see cref="Create"/>
    /// reject it. Purely informational - see <see cref="Create"/>'s remarks for why this can't replace
    /// its own check.
    /// </summary>
    [HttpGet("plan-limit")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<ServerPlanLimitDto>> GetPlanLimit()
    {
        var status = await GetPlanLimitStatusAsync();

        return Ok(new ServerPlanLimitDto
        {
            PlanName = status.Plan?.Name,
            MaximumServers = status.Plan?.MaximumServers,
            CurrentServerCount = status.CurrentServerCount,
            Quantity = status.Quantity,
            CanBuyCapacity = status.CanBuyCapacity
        });
    }

    /// <summary>
    /// Resolves the calling tenant's current Plan (if any - see <see cref="Subscription"/>'s remarks on
    /// why a tenant might genuinely have none) and how many non-deleted servers it currently owns,
    /// shared by <see cref="Create"/>'s enforcement and <see cref="GetPlanLimit"/>'s read-only view of
    /// the exact same numbers.
    /// </summary>
    private async Task<PlanLimitStatus> GetPlanLimitStatusAsync()
    {
        var tenantId = await _tenantContext.GetCurrentTenantIdAsync();

        // No ambient tenant at all (shouldn't happen for an authenticated, EntityAuthorize-gated
        // request, but nothing here depends on that guarantee) - nothing to look up.
        if (tenantId is not { } currentTenantId)
        {
            return new PlanLimitStatus(null, null, 0, null, false);
        }

        var subscription = await _subscriptionRepository.GetForTenantAsync(currentTenantId);

        // The billing period in force is what carries the bought capacity. Null when a subscription
        // somehow has no open period, which the caller treats as "no limit known" and fails open on.
        var currentTerm = await _subscriptionRepository.GetCurrentTermAsync(currentTenantId);

        // GetAllAsync(), not a dedicated count query - IRepository<T> has no CountAsync, and a
        // tenant's server count is small enough (the seeded plans top out at 10) that pulling every
        // row just to count them is cheap relative to everything else these two callers already do.
        var currentServerCount = (await _repository.GetAllAsync()).Count();

        // Capacity is only purchasable where a slot actually costs something - on a flat tier the unit
        // amount is zero and quantity is pinned to what the plan includes, so "buy more" is the wrong
        // thing to offer. Mirrors SubscriptionService.ResolveQuantity.
        var price = currentTerm is null
            ? null
            : subscription?.Plan.Prices.FirstOrDefault(p => p.TermMonths == currentTerm.TermMonths);

        return new PlanLimitStatus(
            currentTenantId, subscription?.Plan, currentServerCount, currentTerm?.Quantity,
            price is { UnitAmount: > 0m });
    }

    private static string CapacityMessage(Plan plan, int slots) =>
        $"You're using all {slots} of your server slot(s) on the {plan.Name} plan. "
        + "Buy more capacity, or upgrade your plan, to add another.";

    private sealed record PlanLimitStatus(
        Guid? TenantId, Plan? Plan, int CurrentServerCount, int? Quantity, bool CanBuyCapacity);

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

        if (!string.IsNullOrEmpty(updateDto.SteamApiKey))
        {
            entity.SteamApiKey = _apiKeyProtector.Protect(ApiKeyProtectorPurposes.SteamApiKey, updateDto.SteamApiKey);
        }

        if (!string.IsNullOrEmpty(updateDto.GeolocationApiKey))
        {
            entity.GeolocationApiKey = _apiKeyProtector.Protect(ApiKeyProtectorPurposes.GeolocationApiKey, updateDto.GeolocationApiKey);
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
                new SendRconCommand(id, request.Command, request.Interactive),
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
    /// The largest <c>pageSize</c> this endpoint honors, regardless of what's requested - a hard cap
    /// against a client (accidentally or otherwise) asking for the entire history table in one call.
    /// </summary>
    private const int MaxEventPageSize = 1000;

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
    /// <param name="isChat">
    /// Filters to chat-only (<c>true</c>) or console-only (<c>false</c>) frames; omitted returns both,
    /// interleaved. The Panel's Console/Chat tabs each pass an explicit value so their history and
    /// line-count controls stay independent of each other.
    /// </param>
    /// <param name="since">Only events captured at or after this instant.</param>
    /// <param name="until">Only events captured at or before this instant.</param>
    /// <param name="includeNonInteractive">
    /// Requests non-interactive (background/potentially privileged) rows in addition to interactive
    /// ones - see <c>RconEvent.Interactive</c>'s remarks. Only ever honored when the caller's own token
    /// says they're a site admin acting as this tenant (see this action's own remarks); anyone else
    /// passing <c>true</c> here silently gets the same interactive-only result as omitting it, rather
    /// than a 403 that would confirm to an unprivileged caller that unfiltered data exists to ask for.
    /// </param>
    [HttpGet("{id}/events")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PagedResult<RconEventDto>>> GetEvents(
        Guid id,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] bool? isChat = null,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] DateTimeOffset? until = null,
        [FromQuery] bool includeNonInteractive = false)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        pageSize = Math.Clamp(pageSize, 1, MaxEventPageSize);

        // Re-derived from the caller's own token every time, never trusted from the request - the
        // whole point of filtering server-side is that a client asking nicely for the unfiltered view
        // is not what grants it. See TokenController.ActingAsClaimType's remarks: only present on a
        // token minted via SiteAdminCrossTenantPolicy, i.e. a site admin currently acting as this
        // server's own tenant - the same population RconHub.JoinUnfilteredServerGroup admits.
        var isActingAsTenant = User.HasClaim(c =>
            c.Type == JumpStart.Services.Authentication.Controllers.TokenController.ActingAsClaimType
            && c.Value == "true");

        var events = await _rconEventRepository.GetForServerAsync(
            id,
            new QueryOptions<RconEvent> { PageNumber = pageNumber, PageSize = pageSize },
            isChat,
            since,
            until,
            includeNonInteractive: includeNonInteractive && isActingAsTenant);

        return Ok(new PagedResult<RconEventDto>
        {
            Items = _mapper.Map<IEnumerable<RconEventDto>>(events.Items),
            TotalCount = events.TotalCount,
            PageNumber = events.PageNumber,
            PageSize = events.PageSize
        });
    }

    /// <summary>
    /// Gets everyone currently connected to this server - sessions with no
    /// <see cref="Data.PlayerSession.DisconnectedAtUtc"/> yet.
    /// </summary>
    [HttpGet("{id}/players")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<IEnumerable<PlayerSessionDto>>> GetCurrentPlayers(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        var players = await _playerSessionRepository.GetCurrentlyConnectedAsync(id);
        return Ok(_mapper.Map<IEnumerable<PlayerSessionDto>>(players));
    }

    /// <summary>
    /// Gets this server's connect/disconnect history, newest-connection-first.
    /// </summary>
    /// <param name="since">Only sessions connected at or after this instant.</param>
    /// <param name="until">Only sessions connected at or before this instant.</param>
    [HttpGet("{id}/players/history")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PagedResult<PlayerSessionDto>>> GetPlayerHistory(
        Guid id,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] DateTimeOffset? until = null)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        pageSize = Math.Clamp(pageSize, 1, MaxEventPageSize);

        var sessions = await _playerSessionRepository.GetForServerAsync(
            id, new QueryOptions<PlayerSession> { PageNumber = pageNumber, PageSize = pageSize }, since, until);

        return Ok(new PagedResult<PlayerSessionDto>
        {
            Items = _mapper.Map<IEnumerable<PlayerSessionDto>>(sessions.Items),
            TotalCount = sessions.TotalCount,
            PageNumber = sessions.PageNumber,
            PageSize = sessions.PageSize
        });
    }

    /// <summary>
    /// Gets this server's kill history, newest first. See <see cref="PlayerKilled"/>'s remarks for why
    /// this is heuristic, not authoritative.
    /// </summary>
    /// <param name="since">Only kills that occurred at or after this instant.</param>
    /// <param name="until">Only kills that occurred at or before this instant.</param>
    [HttpGet("{id}/kills")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PagedResult<PlayerKillEventDto>>> GetKills(
        Guid id,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] DateTimeOffset? until = null)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        pageSize = Math.Clamp(pageSize, 1, MaxEventPageSize);

        var kills = await _playerKillEventRepository.GetForServerAsync(
            id, new QueryOptions<PlayerKillEvent> { PageNumber = pageNumber, PageSize = pageSize }, since, until);

        return Ok(new PagedResult<PlayerKillEventDto>
        {
            Items = _mapper.Map<IEnumerable<PlayerKillEventDto>>(kills.Items),
            TotalCount = kills.TotalCount,
            PageNumber = kills.PageNumber,
            PageSize = kills.PageSize
        });
    }

    /// <summary>
    /// Gets one row per distinct player who has ever connected to this server but isn't currently
    /// connected, newest-last-connection-first - see <see cref="IPlayerSessionRepository.GetInactivePlayersAsync"/>.
    /// </summary>
    [HttpGet("{id}/players/inactive")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PagedResult<InactivePlayerDto>>> GetInactivePlayers(
        Guid id,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 100)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        pageSize = Math.Clamp(pageSize, 1, MaxEventPageSize);

        var inactivePlayers = await _playerSessionRepository.GetInactivePlayersAsync(id, pageNumber, pageSize);
        return Ok(inactivePlayers);
    }

    /// <summary>
    /// The widest <c>since</c>/<c>until</c> range this endpoint honors - a hard cap against a client
    /// asking for a chart's worth of data spanning, say, a full year of 60-second snapshots.
    /// </summary>
    private static readonly TimeSpan MaxServerInfoHistoryRange = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets this server's <c>serverinfo</c> snapshot history (player count, network in/out, memory,
    /// framerate) for the Stats tab's graphs, oldest first. Defaults to the last 24 hours when
    /// <paramref name="since"/> is omitted.
    /// </summary>
    /// <param name="since">Only snapshots captured at or after this instant. Defaults to 24 hours ago.</param>
    /// <param name="until">Only snapshots captured at or before this instant. Defaults to now.</param>
    [HttpGet("{id}/serverinfo/history")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<IEnumerable<ServerInfoSnapshotDto>>> GetServerInfoHistory(
        Guid id,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] DateTimeOffset? until = null)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        var untilValue = until ?? DateTimeOffset.UtcNow;
        var sinceValue = since ?? untilValue - TimeSpan.FromHours(24);

        if (untilValue - sinceValue > MaxServerInfoHistoryRange)
        {
            sinceValue = untilValue - MaxServerInfoHistoryRange;
        }

        var snapshots = await _serverInfoSnapshotRepository.GetForServerAsync(id, sinceValue, untilValue);
        return Ok(_mapper.Map<IEnumerable<ServerInfoSnapshotDto>>(snapshots));
    }

    /// <summary>
    /// The widest <c>since</c>/<c>until</c> range <see cref="GetConnectionLog"/> honors - same
    /// reasoning as <see cref="MaxServerInfoHistoryRange"/>, kept as a separate constant since there is
    /// no reason the two should have to change together.
    /// </summary>
    private static readonly TimeSpan MaxConnectionLogRange = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets this server's Logs tab entries - both WebRCON connection-status transitions (connected,
    /// lost, reconnecting, ...) and worker-side diagnostics that aren't one (a parse error, a poll
    /// failure, ...), newest first - see <see cref="ConnectionLogEntry"/>'s remarks for why this exists
    /// as its own append-only history rather than just the entity's current
    /// <see cref="RustServer.ConnectionStatus"/> column. Defaults to the last 24 hours when
    /// <paramref name="since"/> is omitted.
    /// </summary>
    /// <param name="since">Only entries at or after this instant. Defaults to 24 hours ago.</param>
    /// <param name="until">Only entries at or before this instant. Defaults to now.</param>
    [HttpGet("{id}/connection-log")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<IEnumerable<ConnectionLogEntryDto>>> GetConnectionLog(
        Guid id,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] DateTimeOffset? until = null)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        var untilValue = until ?? DateTimeOffset.UtcNow;
        var sinceValue = since ?? untilValue - TimeSpan.FromHours(24);

        if (untilValue - sinceValue > MaxConnectionLogRange)
        {
            sinceValue = untilValue - MaxConnectionLogRange;
        }

        var entries = await _connectionLogRepository.GetForServerAsync(id, sinceValue, untilValue);
        return Ok(_mapper.Map<IEnumerable<ConnectionLogEntryDto>>(entries));
    }

    /// <summary>
    /// Gets the Oxide/Carbon plugins last reported loaded on this server, ordered by name - see
    /// <see cref="ServerPlugin"/>. Empty when the server has no plugin framework, has none loaded, or
    /// simply hasn't been polled yet (the Worker's first poll runs shortly after it connects).
    /// </summary>
    [HttpGet("{id}/plugins")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<IEnumerable<ServerPluginDto>>> GetPlugins(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        var plugins = await _serverPluginRepository.GetForServerAsync(id);
        return Ok(_mapper.Map<IEnumerable<ServerPluginDto>>(plugins));
    }

    /// <summary>
    /// The RustArchon companion plugin script, with this deployment's public signing key stamped in and a signature
    /// appended - what an admin uploads to <c>carbon/plugins</c> (or <c>oxide/plugins</c>) to install it. The first
    /// request ever creates the deployment's signing key. The file is identical for every tenant (it carries no tenant
    /// data and no secret), so this asks only for the same permission as listing servers. <c>503</c> if the stored
    /// signing key is unusable, which is never "fixed" by silently making a new one.
    /// </summary>
    [HttpGet("plugin/download")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<IActionResult> DownloadPlugin()
    {
        try
        {
            var script = await _pluginScriptService.BuildAsync();

            // Exposed so a caller can show which key this file will trust without parsing it.
            Response.Headers["X-RustArchon-Key-Fingerprint"] = script.KeyFingerprint;
            if (script.PluginVersion is not null)
            {
                Response.Headers["X-RustArchon-Plugin-Version"] = script.PluginVersion;
            }

            return File(script.Bytes, "text/plain; charset=utf-8", "RustArchon.cs");
        }
        catch (PluginSigningKeyException ex)
        {
            _logger.LogError(ex, "The RustArchon plugin could not be signed for download.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The plugin signing key is unavailable. See the Api log.");
        }
    }

    /// <summary>
    /// The RustArchon <b>Updater</b> plugin, stamped and signed like the main one. Installed by hand once; after that it
    /// carries out updates of the main plugin (and is itself only ever replaced by hand, since a failed Updater would
    /// have nothing left to recover it). Same permission and same failure behavior as <see cref="DownloadPlugin"/>.
    /// </summary>
    [HttpGet("plugin/download-updater")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<IActionResult> DownloadPluginUpdater()
    {
        try
        {
            var script = await _pluginScriptService.BuildUpdaterAsync();

            Response.Headers["X-RustArchon-Key-Fingerprint"] = script.KeyFingerprint;
            if (script.PluginVersion is not null)
            {
                Response.Headers["X-RustArchon-Plugin-Version"] = script.PluginVersion;
            }

            return File(script.Bytes, "text/plain; charset=utf-8", "RustArchonUpdater.cs");
        }
        catch (PluginSigningKeyException ex)
        {
            _logger.LogError(ex, "The RustArchon Updater could not be signed for download.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The plugin signing key is unavailable. See the Api log.");
        }
    }

    /// <summary>
    /// Asks the server's Updater plugin to update the RustArchon plugin to the version this Panel serves. Every
    /// precondition is checked first (updates enabled for this server, installed plugin signed by this Panel, Updater
    /// present, a newer version to install) and a refusal comes back as an ordinary <see cref="PluginUpdateResultDto"/>
    /// with a <c>Code</c> saying why. A single-use token is minted only once everything else passes. See
    /// <see cref="PluginUpdateService"/>.
    /// </summary>
    [HttpPost("{id}/plugin/update")]
    [JumpStart.Repositories.EntityAuthorize(action: "Update")]
    public async Task<ActionResult<PluginUpdateResultDto>> StartPluginUpdate(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        return Ok(await _pluginUpdateService.StartAsync(entity));
    }

    /// <summary>
    /// Asks the server's RustArchon plugin to install or update the <b>Updater</b> plugin to the version this Panel serves (the Updater
    /// cannot replace itself; the main plugin does it and puts the old one back if the new one does not come up). Same preconditions,
    /// permission and result shape as <see cref="StartPluginUpdate"/>. See <see cref="PluginUpdateService.StartUpdaterAsync"/>.
    /// </summary>
    [HttpPost("{id}/plugin/update-updater")]
    [JumpStart.Repositories.EntityAuthorize(action: "Update")]
    public async Task<ActionResult<PluginUpdateResultDto>> StartUpdaterUpdate(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        return Ok(await _pluginUpdateService.StartUpdaterAsync(entity));
    }

    /// <summary>
    /// Gets what the optional RustArchon companion plugin last reported on this server - see
    /// <see cref="ServerPluginStatus"/>. <c>204 No Content</c> when the plugin has never answered (not installed,
    /// not yet polled, or too old to understand the handshake). A caller must still confirm the plugin is in the
    /// server's plugin list before trusting this: it can go stale after an uninstall.
    /// </summary>
    [HttpGet("{id}/plugin-status")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<ServerPluginStatusDto>> GetPluginStatus(Guid id)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        var status = await _serverPluginStatusRepository.GetForServerAsync(id);
        if (status is null)
        {
            return NoContent();
        }

        var dto = _mapper.Map<ServerPluginStatusDto>(status);

        // What an update would install, and whether the separate Updater is there to do it.
        dto.LatestPluginVersion = await _pluginScriptService.GetLatestVersionAsync();
        dto.UpdateAvailable = PluginVersions.IsNewer(dto.LatestPluginVersion, dto.PluginVersion);
        var listed = await _serverPluginRepository.GetForServerAsync(id) ?? [];
        var updater = listed.FirstOrDefault(p => string.Equals(p.Name, RustArchonPlugin.UpdaterName, StringComparison.OrdinalIgnoreCase));
        dto.UpdaterInstalled = updater is not null;
        dto.UpdaterVersion = updater?.Version?.TrimStart('v', 'V');
        dto.LatestUpdaterVersion = await _pluginScriptService.GetLatestUpdaterVersionAsync();
        dto.UpdaterUpdateAvailable = PluginVersions.IsNewer(dto.LatestUpdaterVersion, dto.UpdaterVersion);

        // Does the installed plugin trust THIS Panel's key? Only knowable when both sides have one; otherwise null.
        // A read: it must never be what creates this Panel's key.
        var panelFingerprint = await _pluginScriptService.GetKeyFingerprintIfAnyAsync();
        dto.SigningKeyMatchesThisPanel = dto.SigningKeyFingerprint.Length == 0 || panelFingerprint is null
            ? null
            : string.Equals(dto.SigningKeyFingerprint, panelFingerprint, StringComparison.OrdinalIgnoreCase);

        // Where that key stands in this Panel's history (a plain read; unknown or unreadable is empty, never a guess).
        var keyState = dto.SigningState == PluginSigningStates.Valid && dto.SigningKeyFingerprint.Length > 0
            ? await _pluginScriptService.GetKeyStateAsync(dto.SigningKeyFingerprint)
            : null;
        dto.SigningKeyState = keyState?.ToString().ToLowerInvariant() ?? string.Empty;

        return Ok(dto);
    }

    /// <summary>
    /// Saves the RustArchon plugin's two switches (Recording, Combat log) for this server and, if either actually
    /// changed, tells the plugin so right away - see <see cref="ServerPluginSettingsChanged"/>. Returns the
    /// server. Separate from <see cref="Update"/> on purpose: that is a full-record PUT, and these switches must
    /// only ever change because someone changed them (see <see cref="UpdateServerPluginSettingsDto"/>).
    /// </summary>
    [HttpPut("{id}/plugin-settings")]
    [JumpStart.Repositories.EntityAuthorize(action: "Update")]
    public async Task<ActionResult<RustServerDto>> UpdatePluginSettings(Guid id, [FromBody] UpdateServerPluginSettingsDto settings)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        // Only the recording and combat switches are ever pushed to the plugin; the updates switch is a Panel-side
        // permission the plugin never sees, so changing it must not send a command.
        var changed = entity.PluginRecordingEnabled != settings.RecordingEnabled!.Value
            || entity.PluginCombatLogEnabled != settings.CombatLogEnabled!.Value;

        entity.PluginRecordingEnabled = settings.RecordingEnabled!.Value;
        entity.PluginCombatLogEnabled = settings.CombatLogEnabled!.Value;
        entity.PluginUpdatesEnabled = settings.UpdatesEnabled!.Value;
        var updated = await _repository.UpdateAsync(entity);

        // Only when a switch actually changed, and only for a server a Worker is looking after. The consumer
        // reads the saved values, so this message carries none.
        if (changed && updated.IsEnabled)
        {
            await _publishEndpoint.Publish(new ServerPluginSettingsChanged(updated.Id, updated.TenantId));
        }

        return Ok(_mapper.Map<RustServerDto>(updated));
    }

    /// <summary>
    /// Gets one player's full summary on this server - see <see cref="IPlayerSessionRepository.GetPlayerDetailAsync"/>.
    /// </summary>
    [HttpGet("{id}/players/{steamId}")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PlayerDetailDto>> GetPlayerDetail(Guid id, string steamId)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        var detail = await _playerSessionRepository.GetPlayerDetailAsync(id, steamId);
        if (detail is null)
        {
            return NotFound();
        }

        return Ok(detail);
    }

    /// <summary>
    /// Gets one player's session history on this server, newest-connection-first.
    /// </summary>
    [HttpGet("{id}/players/{steamId}/sessions")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PagedResult<PlayerSessionDto>>> GetPlayerSessions(
        Guid id, string steamId, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 100)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        pageSize = Math.Clamp(pageSize, 1, MaxEventPageSize);

        var sessions = await _playerSessionRepository.GetSessionsForPlayerAsync(id, steamId, pageNumber, pageSize);

        return Ok(new PagedResult<PlayerSessionDto>
        {
            Items = _mapper.Map<IEnumerable<PlayerSessionDto>>(sessions.Items),
            TotalCount = sessions.TotalCount,
            PageNumber = sessions.PageNumber,
            PageSize = sessions.PageSize
        });
    }

    /// <summary>
    /// Gets every kill on this server involving this player (as victim or killer), newest first.
    /// </summary>
    [HttpGet("{id}/players/{steamId}/kills")]
    [JumpStart.Repositories.EntityAuthorize(action: "Get")]
    public async Task<ActionResult<PagedResult<PlayerKillEventDto>>> GetPlayerKills(
        Guid id, string steamId, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 100)
    {
        var entity = await _repository.GetByIdAsync(id, null);
        if (entity is null)
        {
            return NotFound();
        }

        pageSize = Math.Clamp(pageSize, 1, MaxEventPageSize);

        var kills = await _playerKillEventRepository.GetForPlayerAsync(id, steamId, pageNumber, pageSize);

        return Ok(new PagedResult<PlayerKillEventDto>
        {
            Items = _mapper.Map<IEnumerable<PlayerKillEventDto>>(kills.Items),
            TotalCount = kills.TotalCount,
            PageNumber = kills.PageNumber,
            PageSize = kills.PageSize
        });
    }

    public class SendCommandRequest
    {
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// Whether a human actually typed/clicked this, as opposed to it being a page-load side effect
        /// or some other backend-initiated fetch reusing this same generic command pathway. No default
        /// on the JSON side either (see <c>SendRconCommand.Interactive</c>'s own remarks for why) -
        /// every caller of this endpoint has to set it deliberately; there's no "probably interactive"
        /// fallback to silently get wrong again.
        /// </summary>
        public required bool Interactive { get; set; }
    }
}
