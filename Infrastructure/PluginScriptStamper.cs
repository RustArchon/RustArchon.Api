// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Cryptography;
using System.Text;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Turns the RustArchon plugin's source into the file this Panel serves: puts this deployment's public signing key
/// into the two placeholder constants and appends one signature line. The plugin's own <c>ArchonIntegrity</c>
/// verifies exactly what this produces, so the two are one contract - see the plugin source for its side, and
/// <c>docs/adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md</c> for why RSA PKCS#1 v1.5.
/// </summary>
/// <remarks>
/// <para>
/// Signed content is every byte of the served file before the final <see cref="SignatureMarker"/> line: the source
/// with the key stamped in, normalized to LF line endings with no byte-order mark and one trailing newline, so the
/// bytes are the same wherever the source was checked out (a Windows checkout may hold CRLF).
/// </para>
/// <para>
/// Stamping insists each placeholder appears <b>exactly once</b>. A source that lacks one would be served with no
/// trusted key and silently behave as an unsigned developer copy; one that has two would be half stamped. Either is
/// a build mistake and must fail loudly here rather than ship.
/// </para>
/// </remarks>
public static class PluginScriptStamper
{
    public const string ModulusPlaceholder = "@@RUSTARCHON_TRUSTED_MODULUS@@";
    public const string ExponentPlaceholder = "@@RUSTARCHON_TRUSTED_EXPONENT@@";
    public const string SignatureMarker = "// RUSTARCHON-SIG-V1: ";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The source with the key stamped in and normalized - the exact bytes that get signed.</summary>
    /// <exception cref="InvalidOperationException">A placeholder is missing or appears more than once.</exception>
    public static byte[] Stamp(string source, string modulusBase64, string exponentBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(modulusBase64);
        ArgumentException.ThrowIfNullOrEmpty(exponentBase64);

        var normalized = source.TrimStart('﻿').Replace("\r\n", "\n");
        if (!normalized.EndsWith('\n'))
        {
            normalized += "\n";
        }

        RequireExactlyOnce(normalized, ModulusPlaceholder);
        RequireExactlyOnce(normalized, ExponentPlaceholder);

        return Utf8NoBom.GetBytes(
            normalized.Replace(ModulusPlaceholder, modulusBase64).Replace(ExponentPlaceholder, exponentBase64));
    }

    /// <summary>The stamped bytes followed by the signature line: the finished file.</summary>
    public static byte[] Attach(byte[] stampedPayload, byte[] signature)
    {
        var line = Encoding.ASCII.GetBytes(SignatureMarker + Convert.ToBase64String(signature) + "\n");
        var file = new byte[stampedPayload.Length + line.Length];
        Buffer.BlockCopy(stampedPayload, 0, file, 0, stampedPayload.Length);
        Buffer.BlockCopy(line, 0, file, stampedPayload.Length, line.Length);
        return file;
    }

    /// <summary>
    /// First 16 hex characters of SHA-256 over the public modulus - short enough to compare by eye, and the same
    /// definition the plugin uses for the fingerprint it reports.
    /// </summary>
    public static string Fingerprint(string modulusBase64) =>
        Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(modulusBase64)).AsSpan(0, 8));

    private static void RequireExactlyOnce(string text, string placeholder)
    {
        var first = text.IndexOf(placeholder, StringComparison.Ordinal);
        if (first < 0)
        {
            throw new InvalidOperationException(
                $"The plugin source has no '{placeholder}' placeholder, so it cannot be stamped with a trusted key.");
        }

        if (text.IndexOf(placeholder, first + placeholder.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"The plugin source contains '{placeholder}' more than once; stamping would leave it half stamped.");
        }
    }
}
