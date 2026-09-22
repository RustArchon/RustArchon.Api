// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// A server's in-game (F7) reports: the moderation inbox. Reading needs <c>RustServer.ViewReports</c>; changing a report's status,
/// assigning it, or adding a note additionally needs <c>RustServer.ManageReports</c>.
/// </summary>
/// <remarks>
/// Reads through the tenant-filtered repository and the tenant-filtered server lookup, so another organization's server or report id
/// is a plain 404 - never an empty answer that would confirm it exists. A report is also required to belong to the server named in
/// the address, so a report id cannot be read through some other server's route.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/reports")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerViewReports)]
public class ServerReportsController(
    IRustServerRepository servers,
    IServerReportRepository reports,
    IServerReportNoteRepository notes,
    IReportAssigneeService assignees,
    IObjectStorage storage,
    IMapper mapper,
    TimeProvider clock) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet]
    public async Task<ActionResult<ServerReportListDto>> List(
        Guid id,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] ServerReportType? type = null,
        [FromQuery] ServerReportStatus? status = null,
        [FromQuery] string? targetSteamId = null)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        var page = await reports.GetForServerAsync(
            id, pageNumber, Math.Clamp(pageSize, 1, MaxPageSize), type, status, targetSteamId);

        return Ok(new ServerReportListDto
        {
            Items = page.Items.Select(mapper.Map<ServerReportDto>).ToList(),
            TotalCount = page.TotalCount,
            PageNumber = page.PageNumber,
            PageSize = page.PageSize
        });
    }

    [HttpGet("count")]
    public async Task<ActionResult<ServerReportCountDto>> Count(Guid id)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        return Ok(new ServerReportCountDto { New = await reports.CountNewAsync(id) });
    }

    [HttpGet("{reportId:guid}")]
    public async Task<ActionResult<ServerReportDto>> Get(Guid id, Guid reportId)
    {
        var report = await FindAsync(id, reportId);
        return report is null ? NotFound() : Ok(mapper.Map<ServerReportDto>(report));
    }

    /// <summary>
    /// The attached screenshot. Private and cacheable: a report's picture never changes, but it must not sit in a shared cache
    /// because it shows a player's screen.
    /// </summary>
    [HttpGet("{reportId:guid}/screenshot")]
    public async Task<IActionResult> Screenshot(Guid id, Guid reportId)
    {
        var report = await FindAsync(id, reportId);
        if (report?.ScreenshotObjectKey is null)
        {
            return NotFound();
        }

        var stored = await storage.GetAsync(report.ScreenshotObjectKey);
        if (stored is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        var contentType = report.ScreenshotObjectKey.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg";
        return File(stored.Content, contentType);
    }

    [HttpPut("{reportId:guid}/status")]
    [RequirePermission(PermissionCatalog.ServerManageReports)]
    public async Task<ActionResult<ServerReportDto>> SetStatus(Guid id, Guid reportId, [FromBody] UpdateServerReportStatusDto request)
    {
        if (!Enum.IsDefined(request.Status))
        {
            return BadRequest("Unknown status.");
        }

        var report = await FindAsync(id, reportId);
        if (report is null)
        {
            return NotFound();
        }

        report.Status = request.Status;
        if (request.Status == ServerReportStatus.New)
        {
            report.ReviewedByUserId = null;
            report.ReviewedAtUtc = null;
        }
        else
        {
            report.ReviewedByUserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var reviewer) ? reviewer : null;
            report.ReviewedAtUtc = clock.GetUtcNow();
        }

        var updated = await reports.UpdateAsync(report);
        return Ok(mapper.Map<ServerReportDto>(updated));
    }

    /// <summary>
    /// Who a report can be assigned to: the organization's active members who hold <c>RustServer.ManageReports</c>, as user ids (the
    /// Api has no names). Needs the manage permission because its only use is choosing an assignee.
    /// </summary>
    [HttpGet("assignees")]
    [RequirePermission(PermissionCatalog.ServerManageReports)]
    public async Task<ActionResult<IReadOnlyList<Guid>>> Assignees(Guid id, CancellationToken cancellationToken)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        return Ok(await assignees.ListAsync(cancellationToken));
    }

    /// <summary>Assigns a report to a member who can act on it, or - with <c>null</c> - takes the assignment back.</summary>
    [HttpPut("{reportId:guid}/assignee")]
    [RequirePermission(PermissionCatalog.ServerManageReports)]
    public async Task<ActionResult<ServerReportDto>> Assign(
        Guid id, Guid reportId, [FromBody] AssignServerReportDto request, CancellationToken cancellationToken)
    {
        var report = await FindAsync(id, reportId);
        if (report is null)
        {
            return NotFound();
        }

        if (request.AssignedToUserId is { } assignee && !await assignees.CanBeAssignedAsync(assignee, cancellationToken))
        {
            return BadRequest("That person cannot be assigned reports.");
        }

        report.AssignedToUserId = request.AssignedToUserId;

        var updated = await reports.UpdateAsync(report);
        return Ok(mapper.Map<ServerReportDto>(updated));
    }

    /// <summary>A report's internal notes, oldest first.</summary>
    [HttpGet("{reportId:guid}/notes")]
    public async Task<ActionResult<IReadOnlyList<ServerReportNoteDto>>> GetNotes(Guid id, Guid reportId)
    {
        if (await FindAsync(id, reportId) is null)
        {
            return NotFound();
        }

        var found = await notes.GetForReportAsync(reportId);
        return Ok(found.Select(mapper.Map<ServerReportNoteDto>).ToList());
    }

    /// <summary>Adds an internal note to a report. Notes are append-only.</summary>
    [HttpPost("{reportId:guid}/notes")]
    [RequirePermission(PermissionCatalog.ServerManageReports)]
    public async Task<ActionResult<ServerReportNoteDto>> AddNote(Guid id, Guid reportId, [FromBody] SaveServerReportNoteDto request)
    {
        var content = request.Content?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            return BadRequest("Enter something for the note to say.");
        }

        if (content.Length > Data.ServerReportNote.MaxContentLength)
        {
            return BadRequest($"A note can be at most {Data.ServerReportNote.MaxContentLength} characters.");
        }

        if (await FindAsync(id, reportId) is null)
        {
            return NotFound();
        }

        var added = await notes.AddAsync(new Data.ServerReportNote { ServerReportId = reportId, Content = content });
        return Ok(mapper.Map<ServerReportNoteDto>(added));
    }

    // Tenant-filtered lookups on both sides, plus the report has to be this server's.
    private async Task<Data.ServerReport?> FindAsync(Guid serverId, Guid reportId)
    {
        if (await servers.GetByIdAsync(serverId, null) is null)
        {
            return null;
        }

        var report = await reports.GetByIdAsync(reportId, null);
        return report is not null && report.RustServerId == serverId ? report : null;
    }
}
