// Copyright ©2026 Scott Blomfield

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>One field a view returns, as the catalog describes it.</summary>
/// <param name="Name">The name in the JSON (camel case).</param>
/// <param name="Type">One of <c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, <c>uuid</c>, <c>datetime</c>, <c>duration</c>, or one of those followed by <c>[]</c>.</param>
/// <param name="Nullable">Whether the value can be null.</param>
/// <param name="Values">For an enum (sent as its name), the names it can take; otherwise <c>null</c>.</param>
public sealed record ViewField(string Name, string Type, bool Nullable, IReadOnlyList<string>? Values = null);

/// <summary>
/// Turns an entity into the flat set of values a diagnostic view returns: every public scalar (or array of scalars) property that is not marked
/// <see cref="DiagnosticsHiddenAttribute"/>. Navigation properties and collections of entities are never followed. What is returned for a type is worked out
/// once and kept, so nothing is reflected per request.
/// </summary>
public static class EntityProjector
{
    private sealed record Accessor(PropertyInfo Property, ViewField Field, bool IsEnum, bool ElementIsEnum);

    private static readonly ConcurrentDictionary<Type, Accessor[]> Accessors = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<ViewField>> Descriptions = new();

    public static bool IsHidden(PropertyInfo property) => property.GetCustomAttribute<DiagnosticsHiddenAttribute>(inherit: true) is not null;

    /// <summary>The fields a view returns for this entity type. The same instance every time.</summary>
    public static IReadOnlyList<ViewField> Describe(Type entityType) =>
        Descriptions.GetOrAdd(entityType, t => AccessorsFor(t).Select(a => a.Field).ToList());

    /// <summary>The values of this entity's visible properties, by their JSON names.</summary>
    public static Dictionary<string, object?> Project(object entity)
    {
        var result = new Dictionary<string, object?>();
        foreach (var accessor in AccessorsFor(entity.GetType()))
        {
            var value = accessor.Property.GetValue(entity);
            result[accessor.Field.Name] = value switch
            {
                null => null,
                var e when accessor.IsEnum => e.ToString(),
                IEnumerable items when accessor.ElementIsEnum => items.Cast<object>().Select(i => i.ToString()).ToArray(),
                _ => value
            };
        }

        return result;
    }

    private static Accessor[] AccessorsFor(Type type) => Accessors.GetOrAdd(type, Build);

    private static Accessor[] Build(Type type)
    {
        var accessors = new List<Accessor>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0 || IsHidden(property))
            {
                continue;
            }

            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null || !property.PropertyType.IsValueType;
            if (TypeName(underlying) is { } scalar)
            {
                var values = underlying.IsEnum ? Enum.GetNames(underlying) : null;
                accessors.Add(new Accessor(property, new ViewField(CamelCase(property.Name), scalar, nullable, values), underlying.IsEnum, false));
            }
            else if (ElementType(underlying) is { } element && TypeName(element) is { } elementName)
            {
                accessors.Add(new Accessor(property, new ViewField(CamelCase(property.Name), elementName + "[]", nullable), false, element.IsEnum));
            }
        }

        return [.. accessors];
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    // null means "not a scalar", so a navigation or a collection of entities is skipped.
    private static string? TypeName(Type type)
    {
        if (type == typeof(string))
        {
            return "string";
        }

        if (type.IsEnum)
        {
            return "string";
        }

        if (type == typeof(Guid))
        {
            return "uuid";
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            return "datetime";
        }

        if (type == typeof(TimeSpan))
        {
            return "duration";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long))
        {
            return "integer";
        }

        return type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : null;
    }

    private static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
