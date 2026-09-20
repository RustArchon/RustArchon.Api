// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Checks a third-party key (Steam Web API, a geolocation provider) with its provider before it is saved onto a server, so the
/// add-server wizard and the edit form can say "this key works" instead of leaving the admin to find out later.
/// </summary>
/// <remarks>
/// Verifying stores nothing. The endpoint is gated on <c>RustServer.Create</c> and rate-limited per caller, so it cannot be used as
/// a free key-testing service; it only ever calls the providers' own fixed addresses. See <see cref="IIntegrationKeyVerifier"/> for
/// how it fails closed.
/// </remarks>
[ApiController]
[Route("api/integrations")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerCreate)]
public class IntegrationVerificationController(IIntegrationKeyVerifier verifier) : ControllerBase
{
    /// <summary>The longest key accepted. Real keys are a few dozen characters.</summary>
    public const int MaxKeyLength = 200;

    [HttpPost("verify-key")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<ActionResult<VerifyIntegrationKeyResultDto>> Verify([FromBody] VerifyIntegrationKeyRequest request)
    {
        Response.Headers.CacheControl = "no-store";

        if (request is null
            || string.IsNullOrWhiteSpace(request.Key)
            || request.Key.Length > MaxKeyLength
            || !Enum.IsDefined(request.Kind)
            || !Enum.IsDefined(request.Provider)
            || (request.Kind == IntegrationKeyKind.Geolocation && request.Provider == GeolocationProviderKind.None))
        {
            return BadRequest();
        }

        var verdict = await verifier.VerifyAsync(request.Kind, request.Provider, request.Key.Trim(), HttpContext.RequestAborted);
        return Ok(new VerifyIntegrationKeyResultDto { Verdict = verdict });
    }

    /// <summary>Name of the rate-limit policy registered in <c>Program.cs</c>.</summary>
    public const string RateLimitPolicy = "verify-key";
}
