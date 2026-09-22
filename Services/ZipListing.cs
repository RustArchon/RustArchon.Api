// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Api.Services;

/// <summary>What looking inside one plugin source file of a zip archive found. Text taken from the file is untrusted.</summary>
/// <param name="Path">The file in the archive.</param>
/// <param name="Class">The class of the first plugin in it, or <c>null</c> if it declares none (a helper source file).</param>
/// <param name="Name">The <c>[Info]</c> name, or <c>null</c>.</param>
/// <param name="Version">The <c>[Info]</c> version, or <c>null</c>.</param>
/// <param name="Problem">Why it cannot be applied (does not read as C#, too large to check, not text); <c>null</c> when it is fine.</param>
public sealed record ZipSourceFinding(string Path, string? Class, string? Name, string? Version, string? Problem);

/// <summary>
/// How an archive's file list and its source-file findings are kept on the lookup row: one line per file, <c>path</c>, a tab, the declared size; and the
/// findings as JSON. Reading tolerates the older form (a bare path per line) so a row saved before sizes were kept still lists its files.
/// </summary>
public static class ZipListing
{
    public static string Format(IEnumerable<ZipEntryInfo> entries) => string.Join('\n', entries.Select(e => $"{e.Path}\t{e.Size}"));

    public static List<ZipEntryInfo> Parse(string? stored)
    {
        var list = new List<ZipEntryInfo>();
        if (string.IsNullOrEmpty(stored))
        {
            return list;
        }

        foreach (var line in stored.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.LastIndexOf('\t');
            if (tab > 0 && long.TryParse(line[(tab + 1)..], out var size))
            {
                list.Add(new ZipEntryInfo(line[..tab], size));
            }
            else
            {
                list.Add(new ZipEntryInfo(line, 0));
            }
        }

        return list;
    }

    public static string FormatFindings(IEnumerable<ZipSourceFinding> findings) => JsonSerializer.Serialize(findings);

    public static List<ZipSourceFinding> ParseFindings(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ZipSourceFinding>>(stored) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
