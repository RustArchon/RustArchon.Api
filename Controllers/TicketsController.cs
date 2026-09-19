// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;
using ApiTicketMessageAuthorType = RustArchon.Api.Data.TicketMessageAuthorType;
using DtoTicketMessageAuthorType = RustArchon.Shared.DTOs.TicketMessageAuthorType;

namespace RustArchon.Api.Controllers;

/// <summary>
/// A tenant's own support tickets - "My Tickets" in the Panel. The submitter is always the caller's
/// own Organization, never a request naming another tenant or another Organization's ticket - see
/// <see cref="ITicketRepository"/>'s remarks on why <see cref="ITicketRepository.GetForTenantAsync"/>
/// specifically, not the ambient query filter, backs <see cref="List"/>.
/// </summary>
/// <remarks>
/// <para>
/// The staff console lives on a separate, platform-permission-gated surface -
/// <see cref="AdminTicketsController"/> - the same split <see cref="OrganizationSettingsController"/>
/// and <see cref="OrganizationsController"/> already draw between "my own" and "any tenant's."
/// </para>
/// <para>
/// <c>TicketMessageAuthorType</c> exists both as an API entity enum (<see cref="RustArchon.Api.Data"/>,
/// aliased here as <c>Api*</c>) and as a DTO enum with the same member names
/// (<see cref="RustArchon.Shared.DTOs"/>, aliased as <c>Dto*</c>) - see its remarks for why. Status is
/// no longer one of these - see <see cref="Data.TicketStatus"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/tickets")]
[Authorize]
[RequirePermission(PermissionCatalog.OrganizationSubmitTickets)]
public class TicketsController(
    ITicketRepository tickets, IQueueRepository queues, ITicketStatusRepository ticketStatuses,
    ITenantContext tenantContext, ICommunicationPublisher communicationPublisher,
    IPlatformSettingsCache settingsCache, IPublishEndpoint publishEndpoint) : ControllerBase
{
    /// <summary>Every active queue, for the "New Ticket" form's picker.</summary>
    [HttpGet("queues")]
    public async Task<ActionResult<IReadOnlyList<QueueDto>>> GetQueues(CancellationToken cancellationToken)
    {
        var found = await queues.GetActiveAsync(cancellationToken);
        return Ok(found.Select(ToQueueDto).ToList());
    }

    /// <summary>The caller's own Organization's tickets, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TicketSummaryDto>>> List(CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        var found = await tickets.GetForTenantAsync(tenantId, cancellationToken);
        return Ok(found.Select(ToSummaryDto).ToList());
    }

    /// <summary>One of the caller's own Organization's tickets, with its full message thread.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TicketDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        var ticket = await tickets.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (ticket is null || ticket.TenantId != tenantId)
        {
            return NotFound();
        }

        var messages = await tickets.GetMessagesAsync(id, cancellationToken);
        return Ok(ToDetailDto(ticket, messages, notes: null));
    }

    /// <summary>Opens a new ticket for the caller's own Organization.</summary>
    [HttpPost]
    public async Task<ActionResult<TicketDetailDto>> Create(
        [FromBody] CreateTicketRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        var queue = await queues.GetByIdAsync(request.QueueId, includes: null);

        if (queue is null || !queue.IsActive)
        {
            return BadRequest("Choose a valid queue.");
        }

        var submittedStatus = await ticketStatuses.GetBySlugAsync(TicketStatusSeeder.Slugs.Submitted, cancellationToken)
            ?? throw new InvalidOperationException("The Submitted ticket status is missing - TicketStatusSeeder should have created it.");

        var now = DateTimeOffset.UtcNow;

        // This app's Identity is username-as-email (Register.razor collects only an email address, no
        // separate display name), and the JWT the Panel mints for its own Api calls carries that as
        // ClaimTypes.Name - never ClaimTypes.Email, which nothing in this token ever populates. Using
        // the latter here silently produced an empty SubmitterEmail, caught live when
        // CommunicationPublisher.QueueInternalAsync's own toAddress guard threw on it.
        var submitterEmail = User.Identity?.Name ?? string.Empty;

        var ticket = new Ticket
        {
            TenantId = tenantId,
            SubmitterUserId = userId,
            SubmitterEmail = submitterEmail,
            SubmitterName = submitterEmail,
            QueueId = request.QueueId,
            Queue = queue,
            Subject = request.Subject.Trim(),
            StatusId = submittedStatus.Id,
            Status = submittedStatus,
            SubmittedOn = now,
            CreatedOn = now,
            CreatedById = userId
        };

        await tickets.AddAsync(ticket);

        var message = await tickets.AddMessageAsync(new TicketMessage
        {
            TicketId = ticket.Id,
            AuthorType = ApiTicketMessageAuthorType.Customer,
            AuthorUserId = userId,
            Body = request.Body.Trim()
        }, cancellationToken);

        await communicationPublisher.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.TicketReceived,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.TicketSubject] = ticket.Subject,
                [EmailTemplateRegistry.Placeholders.TicketLink] = await BuildTicketLinkAsync(ticket.Id)
            },
            ticket.SubmitterEmail, userId, tenantId, cancellationToken: cancellationToken);

        await publishEndpoint.Publish(new TicketCreated(ticket.Id), cancellationToken);

        return Ok(ToDetailDto(ticket, [message], notes: null));
    }

    /// <summary>Adds a reply to one of the caller's own Organization's tickets.</summary>
    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<TicketMessageDto>> AddMessage(
        Guid id, [FromBody] SaveTicketMessageRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        var ticket = await tickets.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (ticket is null || ticket.TenantId != tenantId)
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
            AuthorUserId = userId,
            Body = request.Body.Trim()
        }, cancellationToken);

        if (replyDecision == TicketReplyDecision.Reopened)
        {
            await tickets.SaveAsync(ticket, cancellationToken);
        }

        await publishEndpoint.Publish(new TicketMessageAdded(ticket.Id, message.Id), cancellationToken);

        // No customer-notification email here - staff, not the submitter, needs telling.

        return Ok(ToMessageDto(message));
    }

    /// <summary>The Panel link a "ticket received"/"new reply" notification points at. Only ever built
    /// for a signed-in tenant submitter today - see <see cref="EmailTemplateRegistry.Placeholders.TicketLink"/>'s
    /// remarks for the guest-token equivalent this doesn't yet cover.</summary>
    private async Task<string> BuildTicketLinkAsync(Guid ticketId)
    {
        var configuredPanelBaseUrl = await settingsCache.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl);
        var panelBaseUrl = (configuredPanelBaseUrl is { Length: > 0 }
            ? configuredPanelBaseUrl
            : PlatformSettingsRegistry.DefaultPanelBaseUrl).TrimEnd('/');
        return $"{panelBaseUrl}/Tickets/{ticketId}";
    }

    private static QueueDto ToQueueDto(Queue queue) => new()
    {
        Id = queue.Id,
        Name = queue.Name,
        Slug = queue.Slug,
        Description = queue.Description,
        IsDefault = queue.IsDefault
    };

    private static TicketSummaryDto ToSummaryDto(Ticket ticket) => new()
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
        PreventReopening = ticket.PreventReopening
    };

    private static TicketDetailDto ToDetailDto(
        Ticket ticket, IReadOnlyList<TicketMessage> messages, IReadOnlyList<TicketNote>? notes)
    {
        var summary = ToSummaryDto(ticket);

        return new TicketDetailDto
        {
            Id = summary.Id,
            Subject = summary.Subject,
            Status = summary.Status,
            QueueId = summary.QueueId,
            QueueName = summary.QueueName,
            TenantId = summary.TenantId,
            SubmitterName = summary.SubmitterName,
            SubmitterEmail = summary.SubmitterEmail,
            AssignedToUserId = summary.AssignedToUserId,
            SubmittedOn = summary.SubmittedOn,
            ResolvedOn = summary.ResolvedOn,
            ClosedOn = summary.ClosedOn,
            PreventReopening = summary.PreventReopening,
            Messages = messages.Select(ToMessageDto).ToList(),
            Notes = notes?.Select(ToNoteDto).ToList()
        };
    }

    private static TicketMessageDto ToMessageDto(TicketMessage message) => new()
    {
        Id = message.Id,
        AuthorType = (DtoTicketMessageAuthorType)(int)message.AuthorType,
        AuthorUserId = message.AuthorUserId,
        Body = message.Body,
        CreatedOn = message.CreatedOn
    };

    private static TicketNoteDto ToNoteDto(TicketNote note) => new()
    {
        Id = note.Id,
        AuthorUserId = note.AuthorUserId,
        Content = note.Content,
        CreatedOn = note.CreatedOn
    };

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
