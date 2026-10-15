// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.DiagnosticViews.Views;

/// <summary>
/// <c>server</c>: where one game server stands - its connection and owning worker, its heartbeat, its update switches, and what its organization's plan
/// offers. The server's secrets (RCON password, API keys, report secret) are marked <see cref="DiagnosticsHiddenAttribute"/> on the entity and never come out.
/// </summary>
public sealed class ServerView(ApiDbContext context, IThirdPartyPluginUpdateGate gate, TimeProvider clock) : IDiagnosticView
{
    public static ViewDefinition Definition { get; } = new(
        "server", 1, "Where one game server stands: connection, owning worker, heartbeat, update switches and what its plan offers.",
        [new ViewParameter("serverId", "uuid", Required: true, "The game server's id.")],
        [
            new ViewPartDefinition(
                "server", ViewPartKind.Object, "The server and what is computed about it.", typeof(RustServer),
                [
                    new ViewField("connectionExplanation", "string", false),
                    new ViewField("planOffersThirdPartyPluginUpdates", "boolean", false),
                    new ViewField("thirdPartyGate", "string", false, ["Open", "PlanDoesNotOffer", "NotOptedIn", "HeldForWipe"]),
                    new ViewField("nextWipeUtc", "datetime", false),
                    new ViewField("heldUntilUtc", "datetime", true)
                ])
        ]);

    public async Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken)
    {
        var id = arguments.GetGuid("serverId");
        var server = await context.RustServers.AcrossAllTenants().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ViewAnswer.NotFound("There is no game server with that id.");
        }

        var now = clock.GetUtcNow();
        var gates = await gate.EvaluateAsync(server, now);
        var row = EntityProjector.Project(server);
        row["connectionExplanation"] = Explain(server, now);
        row["planOffersThirdPartyPluginUpdates"] = gates.PlanOffers;
        row["thirdPartyGate"] = gates.State.ToString();
        row["nextWipeUtc"] = gates.NextWipeUtc;
        row["heldUntilUtc"] = gates.HeldUntilUtc;
        return ViewAnswer.Ok(new System.Collections.Generic.Dictionary<string, object?> { ["server"] = row });
    }

    // The status is only as fresh as the last heartbeat: a Worker that stopped leaves "Connected" behind, so an old heartbeat is said out loud.
    private static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromMinutes(5);

    private static string Explain(RustServer server, DateTimeOffset now)
    {
        var heartbeat = server.LastHeartbeatUtc is { } at ? $"last heartbeat {Age(now - at)} ago" : "no heartbeat has ever been recorded";
        if (server.ConnectionStatus == RconConnectionStatus.Connected)
        {
            return server.LastHeartbeatUtc is { } last && now - last > HeartbeatStaleAfter
                ? $"Recorded as connected, but the {heartbeat}, so that record is probably stale (is a Worker running and connected to this server?)."
                : $"Connected; {heartbeat}.";
        }

        return $"Not connected ({server.ConnectionStatus}){(string.IsNullOrEmpty(server.ConnectionStatusDetail) ? string.Empty : ": " + server.ConnectionStatusDetail)}; {heartbeat}.";
    }

    private static string Age(TimeSpan span) => span.TotalHours >= 2
        ? $"{(int)span.TotalHours} hours"
        : span.TotalMinutes >= 2
            ? $"{(int)span.TotalMinutes} minutes"
            : $"{Math.Max(0, (int)span.TotalSeconds)} seconds";
}
