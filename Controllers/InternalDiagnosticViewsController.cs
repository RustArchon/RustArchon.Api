// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Api.DiagnosticViews;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The Api side of the Panel's read-only diagnostic views door (<c>GET /diagnostics/views[/{name}]</c>, ADR-0007). Not reachable from outside: authenticated
/// by the shared internal-service key and called only by the Panel, which carries the caller's log token through in <see cref="TokenHeader"/> - the same
/// single token as the log door.
/// </summary>
/// <remarks>
/// The token is checked first, with the same outcomes as <see cref="InternalLogsController"/>: no token ever created is <c>404</c>, anything else that is not
/// the live token is the same bare <c>401</c>, decided before the view name or its parameters are looked at. Past that, an unknown view or a bad parameter
/// is an error body of one documented shape. The controller writes nothing but its response.
/// </remarks>
[ApiController]
[Route("internal/diagnostics/views")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalDiagnosticViewsController(
    IDiagnosticLogTokenService tokens,
    ViewCatalog catalog,
    IServiceProvider services,
    ViewRunner runner,
    ILogger<InternalDiagnosticViewsController> logger) : ControllerBase
{
    /// <summary>The header the Panel carries the caller's token in. Must match the Panel's <c>LogReadProxy.TokenHeader</c> (the one token serves both doors).</summary>
    public const string TokenHeader = InternalLogsController.TokenHeader;

    [HttpGet]
    public async Task<IActionResult> Catalog([FromHeader(Name = TokenHeader)] string? token)
    {
        Response.Headers.CacheControl = "no-store";
        var (refusal, tokenId) = await CheckTokenAsync(token);
        if (refusal is not null)
        {
            return refusal;
        }

        logger.LogInformation("Diagnostic view catalog read with token {TokenId}.", tokenId);
        return new ObjectResult(catalog.Wire());
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> View(string name, [FromHeader(Name = TokenHeader)] string? token, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var (refusal, tokenId) = await CheckTokenAsync(token);
        if (refusal is not null)
        {
            return refusal;
        }

        var query = Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);
        logger.LogInformation(
            "Diagnostic view {View} read with token {TokenId}: parameters {Parameters}.",
            name, tokenId, string.Join(", ", query.Select(q => $"{q.Key}={Cut(q.Value)}")));

        var definition = catalog.Find(name);
        var view = definition is null ? null : services.ResolveView(name);
        if (definition is null || view is null)
        {
            return Respond(ViewRunner.Error(404, "not_found", "There is no view with that name. GET the catalog for the list."));
        }

        try
        {
            return Respond(await runner.RunAsync(definition, view, query, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            // A view that returns something its definition does not declare is a bug; nothing it returned is sent, and the cause (a field name, never a value) is logged.
            logger.LogError(ex, "Diagnostic view {View} broke its own definition, so its answer was not sent.", name);
            return Respond(ViewRunner.Error(500, "internal_error", "The view failed. The details are in the diagnostic log."));
        }
    }

    private static string Cut(string? value) => value is null ? string.Empty : value.Length <= 60 ? value : value[..60] + "...";

    private static ObjectResult Respond(ViewRunOutcome outcome) => new(outcome.Body) { StatusCode = outcome.Status };

    private async Task<(IActionResult? Refusal, string? TokenId)> CheckTokenAsync(string? token)
    {
        var check = await tokens.CheckAsync(token);
        switch (check.State)
        {
            case DiagnosticLogTokenState.Off:
                return (NotFound(), null);
            case DiagnosticLogTokenState.Invalid:
                // Never the presented value, and nothing that says why.
                logger.LogWarning("A diagnostic view request was refused: the log token was missing or not the live one.");
                return (Unauthorized(), null);
            default:
                return (null, check.TokenId);
        }
    }
}
