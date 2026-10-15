// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>The parameters of one call to a view, checked against the view's definition. Nothing outside the definition gets through.</summary>
public sealed class ViewArguments
{
    public const int MaxStringLength = 200;

    private readonly Dictionary<string, string> _values;

    private ViewArguments(Dictionary<string, string> values) => _values = values;

    public sealed record BindResult(bool Ok, ViewArguments? Arguments, string? Error);

    public static BindResult Bind(IReadOnlyList<ViewParameter> parameters, IReadOnlyDictionary<string, string?> query)
    {
        var known = parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in query.Keys)
        {
            if (!known.ContainsKey(name))
            {
                return Refuse($"Unknown parameter '{Trim(name)}'. See the catalog for what this view takes.");
            }
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            var supplied = query.FirstOrDefault(q => string.Equals(q.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)).Value;
            var text = string.IsNullOrEmpty(supplied) ? parameter.Default : supplied;
            if (string.IsNullOrEmpty(text))
            {
                if (parameter.Required)
                {
                    return Refuse($"Parameter '{parameter.Name}' is required.");
                }

                continue;
            }

            if (text.Length > MaxStringLength || !IsOfType(text, parameter.Type))
            {
                return Refuse($"Parameter '{parameter.Name}' must be a {parameter.Type}.");
            }

            values[parameter.Name] = text;
        }

        return new BindResult(true, new ViewArguments(values), null);
    }

    public string? GetString(string name) => _values.GetValueOrDefault(name);

    public Guid GetGuid(string name) => Guid.Parse(_values[name]);

    public int GetInt(string name) => int.Parse(_values[name], CultureInfo.InvariantCulture);

    public bool GetBool(string name) => bool.Parse(_values[name]);

    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>The parameters as they were bound, for the audit line.</summary>
    public IReadOnlyDictionary<string, string> AsDictionary() => _values;

    private static BindResult Refuse(string error) => new(false, null, error);

    // A parameter name can be anything the caller sent, so it is cut before it is echoed.
    private static string Trim(string text) => text.Length <= 40 ? text : text[..40];

    private static bool IsOfType(string text, string type) => type switch
    {
        "uuid" => Guid.TryParse(text, out _),
        "integer" => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "boolean" => bool.TryParse(text, out _),
        _ => true
    };
}
