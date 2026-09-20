// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// A server's in-game (F7) reports: the moderation inbox. Reading needs <c>RustServer.ViewReports</c>; changing a report's status
/// additionally needs <c>RustServer.ManageReports</c>.
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
