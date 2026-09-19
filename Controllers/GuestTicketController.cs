// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;
using ApiTicketMessageAuthorType = RustArchon.Api.Data.TicketMessageAuthorType;
using DtoTicketMessageAuthorType = RustArchon.Shared.DTOs.TicketMessageAuthorType;

namespace RustArchon.Api.Controllers;

/// <summary>
/// An anonymous submitter's own access to a single ticket, by its unguessable
/// <see cref="Ticket.GuestAccessToken"/> - the account-less counterpart to
/// <see cref="TicketsController"/>. Possession of the token is the only credential there is here; no
/// <c>[Authorize]</c>, no tenant, no user id. Never exposes <see cref="TicketNote"/>s - same rule as
/// <see cref="TicketsController.Get"/>. <see cref="AddMessage"/> applies the same
/// <see cref="TicketReplyPolicy"/> as <see cref="TicketsController.AddMessage"/> - an anonymous
/// submitter is still a "customer" for that policy's purposes.
/// </summary>
[ApiController]
[Route("api/public/tickets/guest")]
[AllowAnonymous]
public class GuestTicketController(
    ITicketRepository tickets, ITicketStatusRepository ticketStatuses, IPublishEndpoint publishEndpoint)
    : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<ActionResult<TicketDetailDto>> Get(string token, CancellationToken cancellationToken)
    {
        var ticket = await tickets.GetByGuestAccessTokenAsync(token, cancellationToken);

        if (ticket is null)
        {
            return NotFound();
        }

        var messages = await tickets.GetMessagesAsync(ticket.Id, cancellationToken);
        return Ok(ToDetailDto(ticket, messages));
    }

    [HttpPost("{token}/messages")]
    public async Task<ActionResult<TicketMessageDto>> AddMessage(
        string token, [FromBody] SaveTicketMessageRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await tickets.GetByGuestAccessTokenAsync(token, cancellationToken);

        if (ticket is null)
        {
            return NotFound();
        }

        var replyDecision = await TicketReplyPolicy.ApplyAsync(ticket, ticketStatuses, cancellationToken);

        if (replyDecision == TicketReplyDecision.Blocked)
        {
            return BadRequest("This ticket is closed and no longer accepts replies.");
        }

        var message = await tickets.AddMessageAsync(new TicketMessage
        {
            TicketId = ticket.Id,
            AuthorType = ApiTicketMessageAuthorType.Customer,
            AuthorUserId = null,
            Body = request.Body.Trim()
        }, cancellationToken);

        if (replyDecision == TicketReplyDecision.Reopened)
        {
            await tickets.SaveAsync(ticket, cancellationToken);
        }

        await publishEndpoint.Publish(new TicketMessageAdded(ticket.Id, message.Id), cancellationToken);

        // No customer-notification email here - same reasoning as TicketsController.AddMessage: staff,
        // not the submitter, needs telling about their own reply.

        return Ok(ToMessageDto(message));
    }

    private static TicketDetailDto ToDetailDto(Ticket ticket, IReadOnlyList<TicketMessage> messages) => new()
    {
        Id = ticket.Id,
        Subject = ticket.Subject,
        Status = TicketStatusMapper.ToDto(ticket.Status),
        QueueId = ticket.QueueId,
        QueueName = ticket.Queue?.Name ?? string.Empty,
        TenantId = ticket.TenantId,
        SubmitterName = ticket.SubmitterName,
        SubmitterEmail = ticket.SubmitterEmail,
        AssignedToUserId = ticket.AssignedToUserId,
        SubmittedOn = ticket.SubmittedOn,
        ResolvedOn = ticket.ResolvedOn,
        ClosedOn = ticket.ClosedOn,
        PreventReopening = ticket.PreventReopening,
        Messages = [.. messages.Select(ToMessageDto)],
        Notes = null
    };

    private static TicketMessageDto ToMessageDto(TicketMessage message) => new()
    {
        Id = message.Id,
        AuthorType = (DtoTicketMessageAuthorType)(int)message.AuthorType,
        AuthorUserId = message.AuthorUserId,
        Body = message.Body,
        CreatedOn = message.CreatedOn
    };
}
