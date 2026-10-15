// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.DiagnosticViews.Views;

/// <summary>
/// <c>server-plugin</c>: what the RustArchon plugin on a game server last reported about itself (version, capabilities, whether its own file verifies, and
/// the key that signed it), whether that key is one this Panel can sign with, and the server's plugin list.
/// </summary>
public sealed class ServerPluginView(ApiDbContext context, IPluginScriptService script, TimeProvider clock) : IDiagnosticView
{
    public static ViewDefinition Definition { get; } = new(
        "server-plugin", 1, "What the RustArchon plugin on a game server reports about itself, whose key signed it, and the server's plugin list.",
        [new ViewParameter("serverId", "uuid", Required: true, "The game server's id.")],
        [
            new ViewPartDefinition(
                "rustarchon", ViewPartKind.Object, "A plain summary of the RustArchon plugin on this server.", null,
                [
                    new ViewField("listed", "boolean", false),
                    new ViewField("reportedRecently", "boolean", false),
                    new ViewField("ageSeconds", "integer", true),
                    new ViewField("explanation", "string", false)
                ]),
            new ViewPartDefinition(
                "status", ViewPartKind.Object, "The plugin's last report.", typeof(ServerPluginStatus),
                [
                    new ViewField("signingKeyIsThisPanels", "boolean", false),
                    new ViewField("signingKeyState", "string", false, ["Active", "Retired", "Revoked", "Unknown"])
                ]),
            new ViewPartDefinition("plugins", ViewPartKind.List, "The server's plugin list.", typeof(ServerPlugin), [])
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
        var status = await context.ServerPluginStatuses.AcrossAllTenants().AsNoTracking().FirstOrDefaultAsync(s => s.RustServerId == id, cancellationToken);
        var plugins = await context.ServerPlugins.AcrossAllTenants().AsNoTracking().Where(p => p.RustServerId == id).OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var listed = RustArchonPlugin.IsListed(plugins.Select(p => p.Name));

        var unavailable = new List<UnavailablePart>();
        Dictionary<string, object?>? statusRow = null;
        if (status is null)
        {
            unavailable.Add(new UnavailablePart("status", "The RustArchon plugin has not reported in yet, or is not installed."));
        }
        else
        {
            var keyState = await script.GetKeyStateAsync(status.SigningKeyFingerprint);
            statusRow = EntityProjector.Project(status);
            statusRow["signingKeyIsThisPanels"] = keyState is not null;
            statusRow["signingKeyState"] = keyState?.ToString() ?? "Unknown";
        }

        var age = status is null ? (TimeSpan?)null : now - status.CapturedAtUtc;
        var recent = age is { } a && a <= ThirdPartyPluginUpdateService.StatusFreshFor;
        var summary = new Dictionary<string, object?>
        {
            ["listed"] = listed,
            ["reportedRecently"] = recent,
            ["ageSeconds"] = age is { } seconds ? (int)seconds.TotalSeconds : null,
            ["explanation"] = Explain(listed, status is not null, recent)
        };

        return ViewAnswer.Ok(
            new Dictionary<string, object?>
            {
                ["rustarchon"] = summary,
                ["status"] = statusRow,
                ["plugins"] = plugins.Select(EntityProjector.Project).ToList()
            },
            unavailable);
    }

    private static string Explain(bool listed, bool reported, bool recent) => (listed, reported, recent) switch
    {
        (false, _, _) => "The RustArchon plugin is not installed on this server (it is not in the server's plugin list).",
        (true, false, _) => "The RustArchon plugin is installed but has not answered yet. This can take a few minutes after it loads.",
        (true, true, false) => "The RustArchon plugin has not been heard from recently, so its last report may be out of date.",
        _ => "The RustArchon plugin is installed and has reported recently."
    };
}
