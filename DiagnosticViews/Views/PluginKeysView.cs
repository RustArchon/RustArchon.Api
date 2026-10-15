// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.DiagnosticViews.Views;

/// <summary>
/// <c>plugin-keys</c>: the RustArchon plugin's signing keys by fingerprint - which is active, which are retired or revoked, and how many servers last reported
/// each. Read through <see cref="IPluginKeyService"/>, which has no key material to return; the key history entity's key columns are also marked
/// <see cref="DiagnosticsHiddenAttribute"/>.
/// </summary>
public sealed class PluginKeysView(IPluginKeyService keys) : IDiagnosticView
{
    public static ViewDefinition Definition { get; } = new(
        "plugin-keys", 1, "The RustArchon plugin's signing keys by fingerprint, their states and how many servers report each. Never key material.",
        [],
        [
            new ViewPartDefinition(
                "keys", ViewPartKind.List, "The keys, newest state first as the Panel lists them.", null,
                [
                    new ViewField("fingerprint", "string", false),
                    new ViewField("state", "string", false, ["Active", "Retired", "Revoked"]),
                    new ViewField("activeSinceUtc", "datetime", true),
                    new ViewField("retiredAtUtc", "datetime", true),
                    new ViewField("revokedAtUtc", "datetime", true),
                    new ViewField("revokedReason", "string", true),
                    new ViewField("serversReporting", "integer", false),
                    new ViewField("lastReportedUtc", "datetime", true)
                ])
        ]);

    public async Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken)
    {
        var list = await keys.ListAsync();
        return ViewAnswer.Ok(new Dictionary<string, object?>
        {
            ["keys"] = list.Select(k => new Dictionary<string, object?>
            {
                ["fingerprint"] = k.Fingerprint,
                ["state"] = k.State.ToString(),
                ["activeSinceUtc"] = k.ActiveSinceUtc,
                ["retiredAtUtc"] = k.RetiredAtUtc,
                ["revokedAtUtc"] = k.RevokedAtUtc,
                ["revokedReason"] = k.RevokedReason,
                ["serversReporting"] = k.ServersReporting,
                ["lastReportedUtc"] = k.LastReportedUtc
            }).ToList()
        });
    }
}
