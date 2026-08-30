// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
/// reachable only from other containers on the compose network. Today this only has the email
/// endpoint; <c>RustArchon.Worker</c>'s <c>IInternalApiClient</c> already expects a
/// <c>GET /internal/rust-servers/{id}</c> action here too, once that side of the RCON pipeline is
/// built - same scheme, same controller, when that happens.
/// </remarks>
[ApiController]
[Route("internal")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalController : ControllerBase
{
    private readonly IPublishEndpoint _publishEndpoint;

    public InternalController(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
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
}
