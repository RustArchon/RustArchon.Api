// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Captcha;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;
using ApiTicketMessageAuthorType = RustArchon.Api.Data.TicketMessageAuthorType;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Anonymous ticket submission from the marketing site's contact form - the account-less counterpart to
/// <see cref="TicketsController.Create"/>. Every request here is unauthenticated by definition, so this
/// is the one ticket-creation path that has to defend itself: a honeypot field, a per-IP rate limit
/// (see <c>Program.cs</c>'s <c>"ticket-submission"</c> policy), and a captcha check, in that order -
/// cheapest and least user-visible checks first, so a bot is turned away before it ever reaches the
/// captcha call or a database write.
/// </summary>
[ApiController]
[Route("api/public/tickets")]
[AllowAnonymous]
[EnableRateLimiting("ticket-submission")]
public class TicketSubmissionController(
    ITicketRepository tickets, IQueueRepository queues, ITicketStatusRepository ticketStatuses,
    ICaptchaVerifierFactory captchaVerifierFactory, ICommunicationPublisher communicationPublisher,
    IPlatformSettingsCache settingsCache, IPublishEndpoint publishEndpoint,
    ILogger<TicketSubmissionController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SubmitPublicTicketResponseDto>> Submit(
        [FromBody] SubmitPublicTicketRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(request.Website))
        {
            // Honeypot tripped - pretend success rather than telling a scripted sender anything is
            // wrong, so it has no signal to adapt against. See this controller's own remarks.
            logger.LogInformation("Ticket submission dropped - honeypot field was filled in.");
            return Ok(new SubmitPublicTicketResponseDto(Guid.NewGuid(), string.Empty));
        }

        var queue = await queues.GetByIdAsync(request.QueueId, includes: null);
        if (queue is null || !queue.IsActive)
        {
            return BadRequest("Choose a valid queue.");
        }

        var captchaProvider = await settingsCache.GetStringAsync(PlatformSettingsRegistry.CaptchaProvider);
        if (captchaProvider != PlatformSettingsRegistry.CaptchaProviders.None)
        {
            if (string.IsNullOrEmpty(request.CaptchaToken))
            {
                return BadRequest("Complete the captcha challenge.");
            }

            var verifier = await captchaVerifierFactory.ResolveAsync();
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

            if (!await verifier.VerifyAsync(request.CaptchaToken, remoteIp, cancellationToken))
            {
                return BadRequest("Captcha verification failed.");
            }
        }

        var submittedStatus = await ticketStatuses.GetBySlugAsync(TicketStatusSeeder.Slugs.Submitted, cancellationToken)
            ?? throw new InvalidOperationException("The Submitted ticket status is missing - TicketStatusSeeder should have created it.");

        var now = DateTimeOffset.UtcNow;

        var ticket = new Ticket
        {
            TenantId = null,
            SubmitterUserId = null,
            SubmitterEmail = request.Email.Trim(),
            SubmitterName = request.Name.Trim(),
            QueueId = request.QueueId,
            Queue = queue,
            Subject = request.Subject.Trim(),
            StatusId = submittedStatus.Id,
            Status = submittedStatus,
            SubmittedOn = now,
            GuestAccessToken = GuestAccessTokenGenerator.New(),
            GuestAccessTokenExpiresOn = now + GuestAccessTokenGenerator.Lifetime,
            CreatedOn = now,
            CreatedById = Guid.Empty
        };

        await tickets.AddAsync(ticket);

        await tickets.AddMessageAsync(new TicketMessage
        {
            TicketId = ticket.Id,
            AuthorType = ApiTicketMessageAuthorType.Customer,
            AuthorUserId = null,
            Body = request.Body.Trim()
        }, cancellationToken);

        var guestLink = await BuildGuestLinkAsync(ticket.GuestAccessToken);

        await communicationPublisher.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.TicketReceived,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.TicketSubject] = ticket.Subject,
                [EmailTemplateRegistry.Placeholders.TicketLink] = guestLink
            },
            ticket.SubmitterEmail, userId: null, tenantId: null, cancellationToken: cancellationToken);

        await publishEndpoint.Publish(new TicketCreated(ticket.Id), cancellationToken);

        return Ok(new SubmitPublicTicketResponseDto(ticket.Id, ticket.GuestAccessToken));
    }

    private async Task<string> BuildGuestLinkAsync(string token)
    {
        var configuredPanelBaseUrl = await settingsCache.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl);
        var panelBaseUrl = (configuredPanelBaseUrl is { Length: > 0 }
            ? configuredPanelBaseUrl
            : PlatformSettingsRegistry.DefaultPanelBaseUrl).TrimEnd('/');
        return $"{panelBaseUrl}/Tickets/Guest/{token}";
    }
}
