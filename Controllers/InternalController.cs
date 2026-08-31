// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Endpoints for RustArchon's own other processes to call, authenticated by shared secret (see
/// <see cref="Infrastructure.Authentication.InternalApiKeyAuthenticationHandler"/>) rather than a
/// user/tenant JWT - there's no end user involved in any of these calls.
/// </summary>
/// <remarks>
/// Not published to the Docker host (see docker-compose.yml's comment on <c>rustarchon-api</c>) -
/// reachable only from other containers on the compose network.
/// </remarks>
[ApiController]
[Route("internal")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalController : ControllerBase
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IRustServerRepository _rustServerRepository;
    private readonly IRconCredentialProtector _rconCredentialProtector;

    public InternalController(
        IPublishEndpoint publishEndpoint,
        IRustServerRepository rustServerRepository,
        IRconCredentialProtector rconCredentialProtector)
    {
        _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        _rustServerRepository = rustServerRepository ?? throw new ArgumentNullException(nameof(rustServerRepository));
        _rconCredentialProtector = rconCredentialProtector ?? throw new ArgumentNullException(nameof(rconCredentialProtector));
    }

    /// <summary>
    /// Queues an email for delivery. Called by the Blazor web app's <c>QueuedEmailSender</c> - it has
    /// no direct broker access itself (see <see cref="RustArchon.Messaging"/>'s own remarks: messaging
    /// infrastructure is Api/Worker only), so this is the seam it goes through instead. Returns as
    /// soon as the message is durably published, not once the email is actually sent - see
    /// <see cref="EmailRequested"/>'s remarks and <c>EmailRequestedConsumer</c> in
    /// <c>RustArchon.Worker</c> for where the actual send happens.
    /// </summary>
    [HttpPost("email")]
    public async Task<IActionResult> SendEmail([FromBody] SendEmailRequestDto request)
    {
        await _publishEndpoint.Publish(new EmailRequested(Guid.NewGuid(), request.To, request.Subject, request.HtmlBody));
        return Accepted();
    }

    /// <summary>
    /// Returns a server's connection details, including its decrypted RCON password, for whichever
    /// <c>RustArchon.Worker</c> instance is trying to claim or refresh its connection. Called from
    /// <c>ConnectToServerConsumer</c> - see <see cref="InternalRustServerInfoDto"/>'s remarks for the
    /// exact shape it expects back.
    /// </summary>
    /// <returns>404 if the server has been deleted or disabled since the caller last knew about it -
    /// the consumer treats that the same as "stop trying to connect."</returns>
    [HttpGet("rust-servers/{id:guid}")]
    public async Task<ActionResult<InternalRustServerInfoDto>> GetServer(Guid id)
    {
        var server = await _rustServerRepository.GetByIdAcrossTenantsAsync(id);
        if (server is null)
        {
            return NotFound();
        }

        return new InternalRustServerInfoDto(
            server.Id,
            server.TenantId,
            server.Host,
            server.Port,
            _rconCredentialProtector.Unprotect(server.RconPassword),
            server.AssignedWorkerId,
            server.LastHeartbeatUtc);
    }
}
