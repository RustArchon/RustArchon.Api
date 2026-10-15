// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.DiagnosticViews.Views;

/// <summary>
/// <c>platform-settings</c>: every platform setting by name. A setting that may be a secret - one of the <see cref="PlatformSettingValueType.Secret"/> type, or
/// whose name says it is a key, token, password, secret or credential whatever type it was declared as - is only ever "set" or "not set", never its value. The
/// entity's <c>Value</c> is marked <see cref="DiagnosticsHiddenAttribute"/>; the <c>value</c> returned here is a computed field that is empty for those.
/// </summary>
public sealed partial class PlatformSettingsView(ApiDbContext context) : IDiagnosticView
{
    public static ViewDefinition Definition { get; } = new(
        "platform-settings", 1, "Every platform setting by name; a setting that may be a secret is only reported as set or not set.",
        [],
        [
            new ViewPartDefinition(
                "settings", ViewPartKind.List, "The platform settings, by category and order.", typeof(PlatformSetting),
                [new ViewField("isSet", "boolean", false), new ViewField("isSecret", "boolean", false), new ViewField("value", "string", true)])
        ]);

    public async Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken)
    {
        var settings = await context.PlatformSettings.AsNoTracking().OrderBy(s => s.Category).ThenBy(s => s.Order).ThenBy(s => s.Key).ToListAsync(cancellationToken);
        return ViewAnswer.Ok(new Dictionary<string, object?> { ["settings"] = settings.Select(Row).ToList() });
    }

    private static Dictionary<string, object?> Row(PlatformSetting setting)
    {
        var secret = setting.ValueType == PlatformSettingValueType.Secret || SecretLookingKey().IsMatch(setting.Key);
        var row = EntityProjector.Project(setting);
        row["isSet"] = !string.IsNullOrEmpty(setting.Value);
        row["isSecret"] = secret;
        row["value"] = secret ? null : setting.Value;
        return row;
    }

    [GeneratedRegex("secret|token|password|passwd|credential|api[-_]?key|private|signingkey", RegexOptions.IgnoreCase)]
    private static partial Regex SecretLookingKey();
}
