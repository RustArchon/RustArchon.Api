// Copyright ©2026 Scott Blomfield

using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Site Admin management of the diagnostic log token (ADR-0006): create (which also rotates), look at, and revoke the one credential that opens the
/// read-only log endpoint. The secret is returned once, by <see cref="Create"/>, and never again.
/// </summary>
/// <remarks>
/// Gated like the rest of platform administration (<c>ManagePlatformSettings</c>): what the token opens is platform-wide logs, never a tenant's.
/// </remarks>
[ApiController]
[Route("api/admin/diagnostics/log-token")]
[Authorize(Policy = "ManagePlatformSettings")]
public class DiagnosticLogAdminController(IDiagnosticLogTokenService tokens) : ControllerBase
{
    private string Actor =>
        User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? User.Identity?.Name ?? "unknown";

    [HttpGet]
    public async Task<ActionResult<DiagnosticLogTokenStatusDto>> Get()
    {
        var status = await tokens.GetStatusAsync();
        return Ok(status is null
            ? new DiagnosticLogTokenStatusDto { Exists = false }
            : new DiagnosticLogTokenStatusDto
            {
                Exists = true,
                TokenId = status.TokenId,
                CreatedAtUtc = status.CreatedAtUtc,
                CreatedBy = status.CreatedBy,
                RevokedAtUtc = status.RevokedAtUtc,
            });
    }

    /// <summary>Creates a token, replacing any existing one at once.</summary>
    [HttpPost]
    public async Task<ActionResult<DiagnosticLogTokenCreatedDto>> Create()
    {
        Response.Headers.CacheControl = "no-store";
        var created = await tokens.CreateAsync(Actor);
        return Ok(new DiagnosticLogTokenCreatedDto { Token = created.Token, TokenId = created.TokenId, CreatedAtUtc = created.CreatedAtUtc });
    }

    [HttpDelete]
    public async Task<IActionResult> Revoke() => await tokens.RevokeAsync(Actor) ? NoContent() : NotFound();
}
