// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The permanent record of every outbound email - the Organization/User admin screens' communications
/// tab reads and acts on this.
/// </summary>
/// <remarks>
/// Platform-wide and cross-tenant on purpose, gated the same way <c>NotesController</c> and
/// <c>OrganizationsController</c> already are - this is the same admin surface, not a tenant-facing
/// feature (a customer never sees their own communications log through this; if that's ever wanted,
/// it's a separate, tenant-scoped endpoint, not this one opened up).
/// </remarks>
[ApiController]
[Route("api/communications")]
[Authorize]
[RequirePermission(PermissionCatalog.PlatformManageOrganizations)]
public class CommunicationsController(ICommunicationRepository communications) : ControllerBase
{
    /// <summary>Communications about an Organization, a member, or both - at least one is required.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CommunicationSummaryDto>>> List(
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        if (tenantId is null && userId is null)
        {
            return BadRequest("Specify an organization, a member, or both.");
        }

        var found = await communications.GetVisibleAsync(tenantId, userId, cancellationToken);
        return Ok(found.Select(ToSummaryDto).ToList());
    }

    /// <summary>One communication in full, including the body actually sent.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CommunicationDetailDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var communication = await communications.GetByIdAcrossTenantsAsync(id, cancellationToken);
        return communication is null ? NotFound() : Ok(ToDetailDto(communication));
    }

    /// <summary>
    /// Withdraws a communication that hasn't sent yet. Refused once it's left Queued - a message a
    /// Worker instance may already be mid-send on, or already reported a result for, isn't something
    /// this can safely un-send; see <see cref="Communication.Status"/>'s remarks on the state machine.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<CommunicationDetailDto>> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var communication = await communications.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (communication is null)
        {
            return NotFound();
        }

        if (communication.Status != Data.CommunicationStatus.Queued)
        {
            return Conflict($"This communication is already {communication.Status} and can't be cancelled.");
        }

        communication.Status = Data.CommunicationStatus.Cancelled;
        communication.CancelledOn = DateTimeOffset.UtcNow;
        await communications.SaveAsync(communication, cancellationToken);

        return Ok(ToDetailDto(communication));
    }

    /// <summary>
    /// Maps by member name rather than a raw enum cast - the two <c>CommunicationStatus</c> enums
    /// (this one and <c>Shared.DTOs.CommunicationStatus</c>) are declared separately on purpose (DTOs
    /// never reference API entity types directly), and a cast would silently break the moment either
    /// one's member order drifted from the other's.
    /// </summary>
    private static Shared.DTOs.CommunicationStatus ToDto(Data.CommunicationStatus status) => status switch
    {
        Data.CommunicationStatus.Queued => Shared.DTOs.CommunicationStatus.Queued,
        Data.CommunicationStatus.Sent => Shared.DTOs.CommunicationStatus.Sent,
        Data.CommunicationStatus.Bounced => Shared.DTOs.CommunicationStatus.Bounced,
        Data.CommunicationStatus.Viewed => Shared.DTOs.CommunicationStatus.Viewed,
        Data.CommunicationStatus.Cancelled => Shared.DTOs.CommunicationStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static CommunicationSummaryDto ToSummaryDto(Communication c) => new()
    {
        Id = c.Id,
        Subject = c.Subject,
        ToAddress = c.ToAddress,
        QueuedOn = c.QueuedOn,
        Status = ToDto(c.Status)
    };

    private static CommunicationDetailDto ToDetailDto(Communication c) => new()
    {
        Id = c.Id,
        Subject = c.Subject,
        ToAddress = c.ToAddress,
        QueuedOn = c.QueuedOn,
        Status = ToDto(c.Status),
        UserId = c.UserId,
        TenantId = c.TenantId,
        HtmlBody = c.HtmlBody,
        SentOn = c.SentOn,
        BouncedOn = c.BouncedOn,
        ViewedOn = c.ViewedOn,
        CancelledOn = c.CancelledOn,
        FailureReason = c.FailureReason,
        CanCancel = c.Status == Data.CommunicationStatus.Queued
    };
}
