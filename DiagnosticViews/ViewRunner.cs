// Copyright ©2026 Scott Blomfield

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>
/// Runs one call to a view: checks the parameters against its definition, runs it, checks that what came back contains only what the catalog says it can
/// (a view that returns something else is a bug, and fails rather than sends it), wraps it in the envelope and keeps it within the size bound.
/// </summary>
public sealed class ViewRunner(TimeProvider clock, int maxBytes = ViewRunner.MaxResponseBytes)
{
    /// <summary>The largest answer sent. Larger is an error, never a silently cut answer.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    public async Task<ViewRunOutcome> RunAsync(
        ViewDefinition definition, IDiagnosticView view, IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken)
    {
        var bound = ViewArguments.Bind(definition.Parameters, query);
        if (!bound.Ok)
        {
            return Error(400, "bad_request", bound.Error!);
        }

        var result = await view.ExecuteAsync(bound.Arguments!, cancellationToken);
        if (result.IsNotFound)
        {
            return Error(404, "not_found", result.NotFoundMessage!);
        }

        CheckConforms(definition, result);

        var body = new Dictionary<string, object?>
        {
            ["view"] = definition.Name,
            ["version"] = definition.Version,
            ["readAtUtc"] = clock.GetUtcNow(),
            ["data"] = result.Data,
            ["unavailable"] = result.Unavailable.Select(u => new Dictionary<string, object?> { ["part"] = u.Part, ["reason"] = u.Reason }).ToList()
        };

        return JsonSerializer.SerializeToUtf8Bytes(body).Length > maxBytes
            ? Error(413, "too_large", $"The answer is larger than {maxBytes / 1024} KiB. Ask for less (a smaller count, or one server at a time).")
            : new ViewRunOutcome(200, body);
    }

    public static ViewRunOutcome Error(int status, string code, string message) => new(
        status, new Dictionary<string, object?> { ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message } });

    // What a view returns is checked against what the catalog promises, so the catalog is true by construction and a field nobody listed cannot leak.
    // The message names the field, never the value.
    private static void CheckConforms(ViewDefinition definition, ViewAnswer result)
    {
        var parts = definition.Parts.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var name in result.Data.Keys.Where(k => !parts.ContainsKey(k)))
        {
            throw new InvalidOperationException($"View '{definition.Name}' returned the part '{name}', which its definition does not declare.");
        }

        foreach (var part in definition.Parts)
        {
            var allowed = ViewCatalog.FieldsOf(part).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            if (!result.Data.TryGetValue(part.Name, out var value) || value is null)
            {
                if (!result.Unavailable.Any(u => u.Part == part.Name))
                {
                    throw new InvalidOperationException($"View '{definition.Name}' returned nothing for the part '{part.Name}' and did not say why.");
                }

                continue;
            }

            var rows = part.Kind == ViewPartKind.List ? AsRows(definition, part, value) : [AsRow(definition, part, value)];
            foreach (var key in rows.SelectMany(r => r.Keys).Where(k => !allowed.Contains(k)).Distinct())
            {
                throw new InvalidOperationException($"View '{definition.Name}' part '{part.Name}' returned the field '{key}', which the catalog does not list.");
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> AsRow(ViewDefinition definition, ViewPartDefinition part, object value) =>
        value as IReadOnlyDictionary<string, object?>
        ?? (value as IDictionary<string, object?>)?.ToDictionary(p => p.Key, p => p.Value)
        ?? throw new InvalidOperationException($"View '{definition.Name}' part '{part.Name}' is not a set of fields.");

    private static List<IReadOnlyDictionary<string, object?>> AsRows(ViewDefinition definition, ViewPartDefinition part, object value) =>
        value is IEnumerable items and not string
            ? [.. items.Cast<object>().Select(i => AsRow(definition, part, i))]
            : throw new InvalidOperationException($"View '{definition.Name}' part '{part.Name}' is not a list.");
}
