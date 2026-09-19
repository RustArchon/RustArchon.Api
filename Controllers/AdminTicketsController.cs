// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
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
/// The staff console's ticket queues - every ticket, across every tenant (and every untenanted
/// prospect submission), for a site admin to work.
/// </summary>
/// <remarks>
/// <para>
/// Platform-wide and cross-tenant on purpose, gated the same way <see cref="NotesController"/> already
/// is - this is one more capability on the same admin surface, not a tenant-facing feature, so it
/// reuses that permission rather than declaring one of its own. <see cref="TicketsController"/> is the
/// tenant-facing "my own tickets" counterpart.
/// </para>
/// <para>See <see cref="TicketsController"/>'s remarks for why <c>TicketMessageAuthorType</c> is
/// aliased <c>Api*</c>/<c>Dto*</c> throughout this file.</para>
/// </remarks>
[ApiController]
[Route("api/admin/tickets")]
[Authorize]
[RequirePermission(PermissionCatalog.PlatformManageOrganizations)]
public class AdminTicketsController(
    ITicketRepository tickets, IQueueRepository queues, ITicketStatusRepository ticketStatuses,
    ICommunicationPublisher communicationPublisher, IPlatformSettingsCache settingsCache,
    IPublishEndpoint publishEndpoint) : ControllerBase
{
    /// <summary>Every active queue, for the ticket detail page's re-routing picker.</summary>
    [HttpGet("queues")]
    public async Task<ActionResult<IReadOnlyList<QueueDto>>> GetQueues(CancellationToken cancellationToken)
    {
        var found = await queues.GetActiveAsync(cancellationToken);
        return Ok(found.Select(ToQueueDto).ToList());
    }

    /// <summary>Every active status, for the status filter and the per-ticket status picker.</summary>
    [HttpGet("statuses")]
    public async Task<ActionResult<IReadOnlyList<TicketStatusDto>>> GetStatuses(CancellationToken cancellationToken)
    {
        var found = await ticketStatuses.GetActiveAsync(cancellationToken);
        return Ok(found.Select(TicketStatusMapper.ToDto).ToList());
    }

    /// <summary>
    /// Every ticket, optionally narrowed to one queue and/or one status. See
    /// <see cref="ITicketRepository.GetForStaffAsync"/>'s remarks for what <paramref name="isClosed"/>
    /// does when <paramref name="statusId"/> is left out - the Open/Closed/All filter keyed off each
    /// status's own <see cref="Data.TicketStatus.IsClosed"/> flag, independent of which exact status a
    /// ticket is in.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TicketSummaryDto>>> List(
        Guid? queueId, Guid? statusId, bool? isClosed, CancellationToken cancellationToken)
    {
        var found = await tickets.GetForStaffAsync(queueId, statusId, isClosed, cancellationToken);
        return Ok(found.Select(ToSummaryDto).ToList());
    }

    /// <summary>One ticket, with its full message thread and its internal notes.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TicketDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await tickets.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (ticket is null)
        {
            return NotFound();
        }

        var messages = await tickets.GetMessagesAsync(id, cancellationToken);
        var notes = await tickets.GetNotesAsync(id, cancellationToken);
        return Ok(ToDetailDto(ticket, messages, notes));
    }

    /// <summary>Changes a ticket's status, queue, or assignee.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TicketSummaryDto>> Update(
        Guid id, [FromBody] UpdateTicketRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await tickets.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (ticket is null)
        {
            return NotFound();
        }

        var newStatus = await ticketStatuses.GetByIdAsync(request.StatusId, includes: null);
        if (newStatus is null || !newStatus.IsActive)
        {
            return BadRequest("Choose a valid status.");
        }

        var now = DateTimeOffset.UtcNow;

        if (newStatus.Slug == TicketStatusSeeder.Slugs.Resolved && ticket.Status.Slug != TicketStatusSeeder.Slugs.Resolved)
        {
            ticket.ResolvedOn = now;
        }
        else if (newStatus.Slug != TicketStatusSeeder.Slugs.Resolved)
        {
            ticket.ResolvedOn = null;
        }

        if (newStatus.Slug == TicketStatusSeeder.Slugs.Closed && ticket.Status.Slug != TicketStatusSeeder.Slugs.Closed)
        {
            ticket.ClosedOn = now;
        }
        else if (newStatus.Slug != TicketStatusSeeder.Slugs.Closed)
        {
            ticket.ClosedOn = null;
        }

        ticket.StatusId = newStatus.Id;
        ticket.Status = newStatus;
        ticket.QueueId = request.QueueId;
        ticket.AssignedToUserId = request.AssignedToUserId;
        ticket.PreventReopening = request.PreventReopening;

        await tickets.SaveAsync(ticket, cancellationToken);

        return Ok(ToSummaryDto(ticket));
    }

    /// <summary>Adds a staff reply to a ticket's customer-visible thread.</summary>
    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<TicketMessageDto>> AddMessage(
        Guid id, [FromBody] SaveTicketMessageRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        var ticket = await tickets.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (ticket is null)
        {
            return NotFound();
        }

        var message = await tickets.AddMessageAsync(new TicketMessage
        {
            TicketId = ticket.Id,
            AuthorType = ApiTicketMessageAuthorType.Staff,
            AuthorUserId = userId,
            Body = request.Body.Trim()
        }, cancellationToken);

        if (ticket.Status.Slug != TicketStatusSeeder.Slugs.WaitingOnCustomer)
        {
            var waitingOnCustomerStatus = await ticketStatuses.GetBySlugAsync(
                TicketStatusSeeder.Slugs.WaitingOnCustomer, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The Waiting on Customer ticket status is missing - TicketStatusSeeder should have created it.");

            ticket.StatusId = waitingOnCustomerStatus.Id;
            ticket.Status = waitingOnCustomerStatus;
            await tickets.SaveAsync(ticket, cancellationToken);
        }

        await communicationPublisher.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.TicketNewReply,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.TicketSubject] = ticket.Subject,
                [EmailTemplateRegistry.Placeholders.TicketLink] = await BuildTicketLinkAsync(ticket.Id)
            },
            ticket.SubmitterEmail, ticket.SubmitterUserId, ticket.TenantId, cancellationToken: cancellationToken);

        await publishEndpoint.Publish(new TicketMessageAdded(ticket.Id, message.Id), cancellationToken);

        return Ok(ToMessageDto(message));
    }

    /// <summary>Adds an internal, staff-only note to a ticket. Never sends a notification.</summary>
    [HttpPost("{id:guid}/notes")]
    public async Task<ActionResult<TicketNoteDto>> AddNote(
        Guid id, [FromBody] SaveTicketNoteRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        var ticket = await tickets.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (ticket is null)
        {
            return NotFound();
        }

        var note = await tickets.AddNoteAsync(new TicketNote
        {
            TicketId = ticket.Id,
            AuthorUserId = userId,
            Content = request.Content.Trim()
        }, cancellationToken);

        return Ok(ToNoteDto(note));
    }

    /// <summary>See <see cref="TicketsController.BuildTicketLinkAsync"/> - same logic, duplicated
    /// rather than shared because the two controllers have no common base to hang it on.</summary>
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
        Ticket ticket, IReadOnlyList<TicketMessage> messages, IReadOnlyList<TicketNote> notes)
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
            Notes = notes.Select(ToNoteDto).ToList()
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
