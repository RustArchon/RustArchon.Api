// Copyright ©2026 Scott Blomfield

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Diagnostics;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The Api side of the Panel's read-only diagnostic log door (<c>GET /diagnostics/logs</c>, ADR-0006). Not reachable from outside: authenticated by
/// the shared internal-service key and called only by the Panel, which carries the caller's log token through in <see cref="TokenHeader"/>.
/// </summary>
/// <remarks>
/// <para>
/// The token is checked first. No token has ever been created: <c>404</c> (the feature does not exist). Anything else that is not the live token -
/// missing, malformed, wrong, replaced, revoked - is the same bare <c>401</c>, decided before the query is even looked at, so a bad token with a bad
/// query reveals nothing more than a bad token alone.
/// </para>
/// <para>
/// Serves the <c>api</c> and <c>worker</c> sources and, on demand, <c>plugin:&lt;server id&gt;</c>. The <c>panel</c> source belongs to the Panel, which
/// merges it in; it is ignored here. The response is read-only by construction: nothing in this controller writes anything but the response.
/// </para>
/// </remarks>
[ApiController]
[Route("internal/logs")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalLogsController(
    IDiagnosticLogTokenService tokens,
    LogRing apiRing,
    WorkerLogStore workers,
    IPluginLogSourceFactory plugins,
    IPlatformSettingsCache settings,
    ILogger<InternalLogsController> logger,
    TimeProvider clock) : ControllerBase
{
    /// <summary>The header the Panel carries the caller's log token in. Must match the Panel's <c>LogReadProxy.TokenHeader</c>.</summary>
    public const string TokenHeader = "X-RustArchon-Log-Token";

    [HttpGet]
    public async Task<IActionResult> Get([FromHeader(Name = TokenHeader)] string? token, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var (refusal, tokenId) = await CheckTokenAsync(token);
        if (refusal is not null)
        {
            return refusal;
        }

        var parameters = Request.Query.ToDictionary(
            q => q.Key, q => (IReadOnlyList<string>)q.Value.Select(v => v ?? string.Empty).ToList(), StringComparer.OrdinalIgnoreCase);
        var parsed = LogQueryParser.Parse(parameters, clock.GetUtcNow());
        if (!parsed.Ok)
        {
            return BadRequest(parsed.Error);
        }

        var request = parsed.Request!;
        var sources = new List<ILogSource>();
        foreach (var name in request.Sources)
        {
            switch (name)
            {
                case "api":
                    sources.Add(new RingLogSource("api", apiRing));
                    break;
                case "worker":
                    sources.AddRange(workers.Sources());
                    break;
                case "panel":
                    // The Panel serves its own process's logs and merges them in.
                    break;
                case var instance when instance.StartsWith("worker:", StringComparison.Ordinal):
                    sources.Add(workers.Sources().FirstOrDefault(w => w.Name == instance)
                        ?? new UnavailableLogSource(instance, "No log batches have been received from that Worker instance (it is not running, was replaced by a newer one, or this Api restarted since)."));
                    break;
                default:
                    sources.Add(plugins.Create(Guid.Parse(name["plugin:".Length..])));
                    break;
            }
        }

        logger.LogInformation(
            "Diagnostic logs read with token {TokenId}: sources {Sources}, level {Level}, category {Category}, text {Text}, count {Count}, mode {Mode}.",
            tokenId,
            string.Join(',', request.Sources),
            request.Query.MinLevel,
            request.Query.Category,
            request.Query.Text,
            request.Query.EffectiveCount,
            request.Query.Summary ? "summary" : "entries");

        var result = await new LogQueryEngine(clock).ExecuteAsync(request.Query, sources, cancellationToken);
        Response.ContentType = "application/x-ndjson";
        await LogNdjsonWriter.WriteAsync(result, Response.Body, cancellationToken);
        return new EmptyResult();
    }

    /// <summary>
    /// The limit the Panel applies per network address to refused requests before they reach this Api (a Platform Setting the Panel cannot read
    /// itself). Not secret and needs no token; reachable only with the internal-service key like everything on this controller.
    /// </summary>
    [HttpGet("limits")]
    public async Task<ActionResult<LogReadLimitsResponse>> Limits() =>
        new LogReadLimitsResponse(await settings.GetPositiveInt32Async(
            PlatformSettingsRegistry.LogReadFailuresPerAddressPerMinute, PlatformSettingsRegistry.DefaultLogReadFailuresPerAddressPerMinute));

    [HttpGet("help")]
    public async Task<IActionResult> Help([FromHeader(Name = TokenHeader)] string? token)
    {
        Response.Headers.CacheControl = "no-store";

        var (refusal, _) = await CheckTokenAsync(token);
        return refusal ?? Content(LogHelp.Text, "text/plain; charset=utf-8");
    }

    private async Task<(IActionResult? Refusal, string? TokenId)> CheckTokenAsync(string? token)
    {
        var check = await tokens.CheckAsync(token);
        switch (check.State)
        {
            case DiagnosticLogTokenState.Off:
                return (NotFound(), null);
            case DiagnosticLogTokenState.Invalid:
                // Never the presented value, and nothing that says why.
                logger.LogWarning("A diagnostic log request was refused: the log token was missing or not the live one.");
                return (Unauthorized(), null);
            default:
                return (null, check.TokenId);
        }
    }
}

/// <summary>Body of <c>GET internal/logs/limits</c>.</summary>
public record LogReadLimitsResponse(int FailuresPerAddressPerMinute);
