// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The Api side of the Panel's public report door: checks the secret a game server presented and, if it is that server's, files the
/// report it sent. Not reachable from outside - authenticated by the shared internal-service key and called only by the Panel's
/// anonymous <c>/ingest/reports/{serverId}/{token}</c> route (ADR-0001), which is what a game server's
/// <c>server.reportsServerEndpoint</c> actually points at.
/// </summary>
/// <remarks>
/// <para>
/// Nothing unauthenticated ever reaches storage or even the request body: the secret arrives in the
/// <see cref="TokenHeader"/> header (the Panel moved it out of the address so it is not recorded in this side's logs), it is checked
/// first, and only then is the form read. Every refusal is the same bare 404 - unknown server, disabled server, server with no
/// secret, wrong secret - so nobody learns which of those it was.
/// </para>
/// <para>
/// The body is held to a ceiling that is a guard against abuse, not a cap on screenshots: it sits far above the largest picture the
/// game sends. It only ever applies to a caller who already holds a good secret.
/// </para>
/// </remarks>
[ApiController]
[Route("internal/reports")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalReportIngestController(
    IReportForwardingService forwarding,
    IReportIngestService ingest,
    IRustServerRepository servers,
    IReportIngestThrottle throttle,
    IPlatformSettingsCache settings) : ControllerBase
{
    /// <summary>The header the Panel carries the secret in.</summary>
    public const string TokenHeader = "X-RustArchon-Report-Token";

    /// <summary>The most a report post may weigh. See the class remarks: abuse guard, not a screenshot cap.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The limit the Panel applies per network address before a report post reaches this Api (a Platform Setting the Panel cannot read
    /// itself). Not secret; reachable only with the internal-service key like everything on this controller.
    /// </summary>
    [HttpGet("limits")]
    public async Task<ActionResult<ReportIngestLimitsResponse>> Limits() =>
        new ReportIngestLimitsResponse(await settings.GetPositiveInt32Async(
            PlatformSettingsRegistry.ReportsPerAddressPerMinute, PlatformSettingsRegistry.DefaultReportsPerAddressPerMinute));

    [HttpPost("{serverId:guid}")]
    [RequestSizeLimit(MaxBytes)]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = MaxBytes)]
    public async Task<IActionResult> Ingest(Guid serverId, [FromHeader(Name = TokenHeader)] string? token)
    {
        Response.Headers.CacheControl = "no-store";

        if (string.IsNullOrEmpty(token) || token.Length > ReportForwardingService.MaxTokenLength)
        {
            return NotFound();
        }

        // Before the body is read, so an unauthenticated caller cannot make this touch a byte of it.
        if (!await forwarding.IsTokenValidAsync(serverId, token))
        {
            return NotFound();
        }

        var server = await servers.GetByIdAcrossTenantsAsync(serverId);
        if (server is null)
        {
            return NotFound();
        }

        // Only a caller with a good secret can spend this server's budget.
        var perMinute = await settings.GetPositiveInt32Async(
            PlatformSettingsRegistry.ReportsPerServerPerMinute, PlatformSettingsRegistry.DefaultReportsPerServerPerMinute);
        if (!throttle.TryAcquire(serverId, perMinute))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!Request.HasFormContentType)
        {
            return BadRequest();
        }

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        await ingest.IngestNativeAsync(server, form["userid"].ToString(), form["data"].ToString(), HttpContext.RequestAborted);

        return NoContent();
    }
}

/// <summary>What the Panel's report door asks the Api for: how many posts one address may make per minute.</summary>
public record ReportIngestLimitsResponse(int PerAddressPerMinute);

