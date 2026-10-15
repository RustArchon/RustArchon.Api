// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>
/// The catalog of diagnostic views: what each is called, takes and can return, so a client needs nothing else to call them (ADR-0007). Immutable and built once,
/// at startup, from the views' static definitions; requests only read it. A definition the catalog cannot describe (a duplicate name, no version, a parameter
/// type it does not know) fails construction, so it fails the Api's boot rather than the first caller.
/// </summary>
public sealed partial class ViewCatalog
{
    private static readonly HashSet<string> ParameterTypes = ["string", "integer", "uuid", "boolean"];

    private readonly Dictionary<string, ViewDefinition> _definitions;
    private readonly IReadOnlyList<ViewDescription> _descriptions;
    private readonly object _wire;

    public ViewCatalog(IEnumerable<ViewDefinition> definitions)
    {
        _definitions = new Dictionary<string, ViewDefinition>(StringComparer.Ordinal);
        var descriptions = new List<ViewDescription>();
        foreach (var definition in definitions)
        {
            Validate(definition);
            if (!_definitions.TryAdd(definition.Name, definition))
            {
                throw new InvalidOperationException($"Two diagnostic views are both called '{definition.Name}'.");
            }

            descriptions.Add(Describe(definition));
        }

        _descriptions = [.. descriptions.OrderBy(d => d.Name, StringComparer.Ordinal)];
        _wire = new Dictionary<string, object?> { ["views"] = _descriptions.Select(ToWire).ToList() };
    }

    public IReadOnlyList<ViewDescription> Describe() => _descriptions;

    /// <summary>
    /// The catalog as it goes over the wire: plain dictionaries and lists with fixed lower-camel-case names, built once, so the format a client (or a future MCP
    /// server) reads does not depend on how the Api's JSON options happen to be set.
    /// </summary>
    public object Wire() => _wire;

    public ViewDefinition? Find(string name) => _definitions.GetValueOrDefault(name);

    /// <summary>The fields a part can return: the entity's non-hidden properties, then the computed ones.</summary>
    public static IReadOnlyList<ViewField> FieldsOf(ViewPartDefinition part) =>
        [.. (part.EntityType is null ? [] : EntityProjector.Describe(part.EntityType)), .. part.ExtraFields];

    private static Dictionary<string, object?> ToWire(ViewDescription view) => new()
    {
        ["name"] = view.Name,
        ["version"] = view.Version,
        ["description"] = view.Description,
        ["parameters"] = view.Parameters.Select(p => new Dictionary<string, object?>
        {
            ["name"] = p.Name, ["type"] = p.Type, ["required"] = p.Required, ["description"] = p.Description, ["default"] = p.Default
        }).ToList(),
        ["parts"] = view.Parts.Select(p => new Dictionary<string, object?>
        {
            ["name"] = p.Name,
            ["kind"] = p.Kind,
            ["description"] = p.Description,
            ["fields"] = p.Fields.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name, ["type"] = f.Type, ["nullable"] = f.Nullable, ["values"] = f.Values
            }).ToList()
        }).ToList()
    };

    private static ViewDescription Describe(ViewDefinition definition) => new(
        definition.Name, definition.Version, definition.Description, definition.Parameters,
        [.. definition.Parts.Select(p => new ViewPartDescription(p.Name, p.Kind == ViewPartKind.List ? "list" : "object", p.Description, FieldsOf(p)))]);

    private static void Validate(ViewDefinition definition)
    {
        if (!ViewName().IsMatch(definition.Name))
        {
            throw new InvalidOperationException($"'{definition.Name}' is not a valid view name (lower-case words joined by hyphens).");
        }

        if (definition.Version < 1)
        {
            throw new InvalidOperationException($"View '{definition.Name}' has no version.");
        }

        foreach (var parameter in definition.Parameters)
        {
            if (!ParameterTypes.Contains(parameter.Type))
            {
                throw new InvalidOperationException($"View '{definition.Name}' parameter '{parameter.Name}' has a type the catalog cannot describe: '{parameter.Type}'.");
            }
        }

        var partNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in definition.Parts)
        {
            if (!partNames.Add(part.Name))
            {
                throw new InvalidOperationException($"View '{definition.Name}' declares the part '{part.Name}' twice.");
            }

            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in FieldsOf(part))
            {
                if (!fieldNames.Add(field.Name))
                {
                    throw new InvalidOperationException($"View '{definition.Name}' part '{part.Name}' has the field '{field.Name}' twice.");
                }
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex ViewName();
}
