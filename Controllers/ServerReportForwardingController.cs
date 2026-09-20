// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The address an admin pastes into their game server's <c>server.reportsServerEndpoint</c> so it forwards F7 reports here, and the
/// checks around it (ADR-0001).
/// </summary>
/// <remarks>
/// Needs <c>RustServer.ManageReportForwarding</c>: the address contains the secret. Responses are never cached, and nothing here
/// logs the address.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/report-forwarding")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerManageReportForwarding)]
public class ServerReportForwardingController(IReportForwardingService forwarding) : ControllerBase
{
    /// <summary>The address and its state. The first ask is what mints the secret.</summary>
    [HttpGet]
    public async Task<ActionResult<ReportForwardingDto>> Get(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await forwarding.GetAsync(id);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Replaces the secret; the previous address stops working immediately.</summary>
    [HttpPost("rotate")]
    public async Task<ActionResult<ReportForwardingDto>> Rotate(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await forwarding.RotateAsync(id);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Asks the game server what it is set to and compares.</summary>
    [HttpPost("verify")]
    public async Task<ActionResult<VerifyReportForwardingResultDto>> Verify(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await forwarding.VerifyAsync(id);
        return result is null ? NotFound() : Ok(result);
    }
}
