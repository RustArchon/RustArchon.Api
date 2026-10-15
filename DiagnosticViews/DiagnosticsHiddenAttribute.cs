// Copyright ©2026 Scott Blomfield

using System;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>
/// Marks an entity property that the read-only diagnostic views must never return (ADR-0007). This attribute is the definitive list of what is never sent:
/// a view returns a viewed entity's properties except the ones marked here. Put it on anything secret (passwords, API keys, tokens and their hashes,
/// encrypted values, key material, billing data) and on anything too bulky to be worth sending, and say why, so the next reader does not have to guess.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class DiagnosticsHiddenAttribute : Attribute
{
    public DiagnosticsHiddenAttribute(string reason)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? throw new ArgumentException("Say why this property is hidden.", nameof(reason)) : reason;
    }

    public string Reason { get; }
}
