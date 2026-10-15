// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>A parameter a view takes. <paramref name="Type"/> is one of <c>string</c>, <c>integer</c>, <c>uuid</c>, <c>boolean</c>.</summary>
public sealed record ViewParameter(string Name, string Type, bool Required, string Description, string? Default = null);

public enum ViewPartKind
{
    /// <summary>One set of fields.</summary>
    Object,

    /// <summary>A list of sets of fields, all of the same shape.</summary>
    List
}

/// <summary>
/// One named piece of a view's data: the non-hidden properties of an entity (if <paramref name="EntityType"/> is given) plus any computed fields.
/// The catalog and the check on what a view returns are both worked out from this.
/// </summary>
public sealed record ViewPartDefinition(string Name, ViewPartKind Kind, string Description, Type? EntityType, IReadOnlyList<ViewField> ExtraFields);

/// <summary>Everything about a view that does not need a database: what it is called, what it takes and what it can return.</summary>
public sealed record ViewDefinition(
    string Name, int Version, string Description, IReadOnlyList<ViewParameter> Parameters, IReadOnlyList<ViewPartDefinition> Parts);

/// <summary>A part as the catalog describes it.</summary>
public sealed record ViewPartDescription(string Name, string Kind, string Description, IReadOnlyList<ViewField> Fields);

/// <summary>A view as the catalog describes it: enough for a client to call it and read the answer without anything else.</summary>
public sealed record ViewDescription(
    string Name, int Version, string Description, IReadOnlyList<ViewParameter> Parameters, IReadOnlyList<ViewPartDescription> Parts);

/// <summary>A part of a view's answer that could not be read, and why - never an empty value standing in for it.</summary>
public sealed record UnavailablePart(string Part, string Reason);

/// <summary>What a view returns: the data by part name, parts it could not read, or that the thing asked about does not exist.</summary>
public sealed class ViewAnswer
{
    private ViewAnswer(IReadOnlyDictionary<string, object?> data, IReadOnlyList<UnavailablePart> unavailable, string? notFound)
    {
        Data = data;
        Unavailable = unavailable;
        NotFoundMessage = notFound;
    }

    public IReadOnlyDictionary<string, object?> Data { get; }

    public IReadOnlyList<UnavailablePart> Unavailable { get; }

    public string? NotFoundMessage { get; }

    public bool IsNotFound => NotFoundMessage is not null;

    public static ViewAnswer Ok(IReadOnlyDictionary<string, object?> data, IReadOnlyList<UnavailablePart>? unavailable = null) =>
        new(data, unavailable ?? [], null);

    public static ViewAnswer NotFound(string message) => new(new Dictionary<string, object?>(), [], message);
}

/// <summary>The HTTP status and body a view run comes to. The body is plain dictionaries and lists, so it serializes the same way whatever the JSON options.</summary>
public sealed record ViewRunOutcome(int Status, object Body);
