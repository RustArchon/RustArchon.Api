// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Api.Services;

/// <summary>What looking at a downloaded plugin file found. Text taken from the file (the <c>Info*</c> fields, entry names) is untrusted.</summary>
public sealed record PluginFileInspection(
    PluginFileValidationState State,
    string Kind,
    string Sha256,
    long SizeBytes,
    string Reason,
    string? ClassName = null,
    string? InfoName = null,
    string? InfoAuthor = null,
    string? InfoVersion = null,
    IReadOnlyList<ZipEntryInfo>? ZipEntries = null,
    IReadOnlyList<ZipSourceFinding>? ZipSourceFindings = null);

/// <summary>Decides whether a downloaded file is something that can be applied to a server as a plugin.</summary>
public interface IPluginFileInspector
{
    /// <summary>
    /// Looks at <paramref name="content"/>: hashes it, works out what it is, and for a plugin source file checks that it reads as C# a game server
    /// can compile, that it is the plugin <paramref name="expectedNormalizedName"/>, and that it is not older than <paramref name="expectedVersion"/>.
    /// Nothing in the file is run, and nothing is extracted from an archive.
    /// </summary>
    PluginFileInspection Inspect(byte[] content, string expectedNormalizedName, string expectedVersion);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>The compile check is a syntax-level one, by design.</b> A real compile needs the game's own assemblies (Rust's and Carbon's, some 200 files
/// and hundreds of megabytes that are the game's, not ours), which the Api does not and should not hold. What reading alone can tell is what usually
/// goes wrong with a download: an HTML page saved in place of the file (a login wall, an error page) and a truncated or mangled file - see
/// <see cref="PluginSourceSyntax"/>. It reads the file as the newest C#, not as the old version the RustArchon plugin itself is held to: real uMod
/// plugins use modern syntax that Carbon compiles, so a language-version limit would reject good files (found by trying real ones). What it cannot tell
/// is a call to something the game no longer has, or syntax a particular server's compiler is too old for; that is found by the server's own
/// compiler when the file is applied, and the previous file is put back if it does not load.
/// </para>
/// <para>
/// <b>A zip</b> is listed (each file's path and declared size) and its plugin source files are read once: which is the plugin, its version, and whether
/// each reads as C#. Which files go where is a person's call, made per server, and until they have made it nothing in it is applied; but what a source
/// file <i>is</i> does not depend on where it goes, so it is worked out here, once, with the hash, and their instructions are checked against it later.
/// Nothing is ever unpacked to disk, no file is run, and a size is never taken on trust: every source file is read through a bound. Program files
/// (<c>.dll</c> and the like) are only listed: they are never installed from an archive, and the person's instructions are refused if they try.
/// </para>
/// </remarks>
public class PluginFileInspector : IPluginFileInspector
{
    // The base classes a plugin extends. [Info] alone is not enough (other things are attributed with it); a plugin also extends one of these.
    private static readonly string[] PluginBases = ["RustPlugin", "CovalencePlugin", "CarbonPlugin", "Plugin"];

    public PluginFileInspection Inspect(byte[] content, string expectedNormalizedName, string expectedVersion)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var size = content.LongLength;

        if (content.Length == 0)
        {
            return new PluginFileInspection(PluginFileValidationState.Invalid, "other", sha, size, "the download is empty");
        }

        if (IsZip(content))
        {
            return InspectZip(content, sha, size, expectedNormalizedName, expectedVersion);
        }

        if (!TryReadText(content, out var source))
        {
            return new PluginFileInspection(PluginFileValidationState.Invalid, "other", sha, size, "the download is not text, so it is not a plugin source file");
        }

        if (LooksLikeHtml(source))
        {
            return new PluginFileInspection(PluginFileValidationState.Invalid, "other", sha, size, "the download is a web page, not a plugin file (a login or error page)");
        }

        // As the newest C#, not the old one the plugin's own file is held to: only a real syntax error counts. The popular plugins already use modern
        // syntax (found by trying real uMod files), and what a given server's compiler accepts is that server's to say - and roll back on.
        var problems = PluginSourceSyntax.Problems(source, LanguageVersion.Latest, out var total);
        if (problems.Count > 0)
        {
            var more = total > 1 ? $" (and {total - 1} more)" : string.Empty;
            return new PluginFileInspection(PluginFileValidationState.Invalid, "cs", sha, size, Trim($"does not read as C# a game server can compile: {problems[0]}{more}", 300));
        }

        var info = FindPluginInfo(source);
        if (info is null)
        {
            return new PluginFileInspection(PluginFileValidationState.Invalid, "cs", sha, size, "no plugin class with an [Info] attribute, so it is not a plugin");
        }

        var (className, name, author, version) = info.Value;
        var named = Letters(className) == expectedNormalizedName || Letters(name) == expectedNormalizedName;
        if (!named)
        {
            return new PluginFileInspection(
                PluginFileValidationState.Invalid, "cs", sha, size, Trim($"the file is the plugin \"{name}\", not the one asked for", 300), className, name, author, version);
        }

        if (PluginVersions.IsValid(version) && PluginVersions.IsValid(expectedVersion) && !PluginVersions.IsAtLeast(version, expectedVersion))
        {
            return new PluginFileInspection(
                PluginFileValidationState.Invalid, "cs", sha, size, Trim($"the file is version {version}, older than the update ({expectedVersion})", 300), className, name, author, version);
        }

        return new PluginFileInspection(PluginFileValidationState.Valid, "cs", sha, size, "a plugin source file that reads cleanly", className, name, author, version);
    }

    /// <summary>
    /// Reads the bytes as source text, or says they are not text. Real plugin files are not always clean UTF-8 - a stray Windows-1252 dash is common and
    /// the game server compiles them anyway - so a bad byte is not held against a file; what does count is a NUL (source has none; UTF-16 is recognised
    /// by its byte order mark), or a body that is mostly control characters or undecodable bytes, which is a binary file.
    /// </summary>
    private static bool TryReadText(byte[] content, out string text)
    {
        text = string.Empty;
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(content, 2, content.Length - 2);
            return true;
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);
            return true;
        }

        var controls = 0;
        foreach (var b in content)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 32 && b != '\t' && b != '\n' && b != '\r' && b != '\f')
            {
                controls++;
            }
        }

        if (controls * 100 > content.Length)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var replaced = decoded.Count(c => c == '�');
        if (replaced * 20 > decoded.Length)      // more than one character in twenty was undecodable: not text
        {
            return false;
        }

        text = decoded;
        return true;
    }

    private static bool IsZip(byte[] content) =>
        content.Length >= 4 && content[0] == 'P' && content[1] == 'K' && (content[2] == 3 || content[2] == 5 || content[2] == 7) && (content[3] == 4 || content[3] == 6 || content[3] == 8);

    /// <summary>The most a single plugin source file inside an archive may be to be read for checking. A safety bound on this server's memory, not a limit on plugins.</summary>
    public const int MaxZipSourceBytes = 8 * 1024 * 1024;

    /// <summary>The most source read from one archive in total, for the same reason.</summary>
    public const int MaxZipTotalSourceBytes = 64 * 1024 * 1024;

    /// <summary>
    /// How many files an archive's central directory lists, counted here by walking its records - never from the count in the archive's own header,
    /// which a hostile archive can set to anything - and stopping as soon as it passes <paramref name="stopAfter"/>. <c>null</c> if it is not a plain
    /// (non-zip64) archive. Done before <see cref="ZipArchive"/> is given the bytes, because that reads every record into memory.
    /// </summary>
    internal static int? CountCentralDirectoryFiles(byte[] c, int stopAfter)
    {
        var eocd = -1;
        for (var i = c.Length - 22; i >= Math.Max(0, c.Length - 22 - 65535); i--)
        {
            if (c[i] == 0x50 && c[i + 1] == 0x4b && c[i + 2] == 5 && c[i + 3] == 6)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            return null;
        }

        long position = BitConverter.ToUInt32(c, eocd + 16);
        var count = 0;
        while (position + 46 <= c.Length && c[position] == 0x50 && c[position + 1] == 0x4b && c[position + 2] == 1 && c[position + 3] == 2)
        {
            if (++count > stopAfter)
            {
                return count;
            }

            position += 46 + BitConverter.ToUInt16(c, (int)position + 28) + BitConverter.ToUInt16(c, (int)position + 30) + BitConverter.ToUInt16(c, (int)position + 32);
        }

        return count;
    }

    private static PluginFileInspection InspectZip(byte[] content, string sha, long size, string expectedNormalizedName, string expectedVersion)
    {
        var counted = CountCentralDirectoryFiles(content, ZipMapping.MaxArchiveFiles);
        if (counted is null)
        {
            return new PluginFileInspection(PluginFileValidationState.Invalid, "zip", sha, size, "the download looks like a zip archive but cannot be read");
        }

        if (counted > ZipMapping.MaxArchiveFiles)
        {
            return new PluginFileInspection(PluginFileValidationState.Invalid, "zip", sha, size, $"the archive holds more than {ZipMapping.MaxArchiveFiles} files, which no plugin needs");
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var files = archive.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/')).ToList();

            // A path that leaves the folder it is unpacked into is how a hostile archive overwrites something else. Refused at the door,
            // rather than trusted to be caught whenever it is finally applied.
            var unsafeName = files.Select(e => e.FullName).FirstOrDefault(IsUnsafeEntryName);
            if (unsafeName is not null)
            {
                return new PluginFileInspection(PluginFileValidationState.Invalid, "zip", sha, size, Trim($"the archive has a file whose path is not safe: {unsafeName}", 300));
            }

            var listed = files.Take(PluginDownloadLookup.MaxZipEntries).Select(e => new ZipEntryInfo(ZipMapping.NormalizePath(e.FullName), e.Length)).ToList();
            var more = files.Count > listed.Count ? $" (first {listed.Count} listed)" : string.Empty;

            var budget = (long)MaxZipTotalSourceBytes;
            var findings = new List<ZipSourceFinding>();
            foreach (var entry in files.Where(e => e.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Take(PluginDownloadLookup.MaxZipEntries))
            {
                findings.Add(InspectZipSource(entry, ref budget));
            }

            // Which of the archive's plugin files is the plugin that was asked for, and is it fit to apply? Instructions are checked against this later.
            var main = findings.FirstOrDefault(f => f.Class is not null && (Letters(f.Class) == expectedNormalizedName || Letters(f.Name ?? string.Empty) == expectedNormalizedName));
            if (main is null)
            {
                var reason = findings.Any(f => f.Problem is not null)
                    ? $"the archive has no plugin file for the plugin asked for that reads as C# ({Trim(findings.First(f => f.Problem is not null).Problem!, 120)})"
                    : "the archive has no plugin file for the plugin asked for";
                return new PluginFileInspection(PluginFileValidationState.Invalid, "zip", sha, size, Trim(reason, 300), ZipEntries: listed, ZipSourceFindings: findings);
            }

            if (main.Problem is not null)
            {
                return new PluginFileInspection(
                    PluginFileValidationState.Invalid, "zip", sha, size, Trim($"the plugin file in the archive is not one a game server can compile: {main.Problem}", 300),
                    main.Class, main.Name, null, main.Version, listed, findings);
            }

            if (main.Version is not null && PluginVersions.IsValid(main.Version) && PluginVersions.IsValid(expectedVersion) && !PluginVersions.IsAtLeast(main.Version, expectedVersion))
            {
                return new PluginFileInspection(
                    PluginFileValidationState.Invalid, "zip", sha, size, Trim($"the plugin in the archive is version {main.Version}, older than the update ({expectedVersion})", 300),
                    main.Class, main.Name, null, main.Version, listed, findings);
            }

            return new PluginFileInspection(
                PluginFileValidationState.NeedsInstructions, "zip", sha, size,
                $"a zip archive of {files.Count} files; it is applied only once someone says which go where{more}",
                main.Class, main.Name, null, main.Version, listed, findings);
        }
        catch (InvalidDataException)
        {
            return new PluginFileInspection(PluginFileValidationState.Invalid, "zip", sha, size, "the download looks like a zip archive but cannot be read");
        }
    }

    /// <summary>
    /// Reads one source file out of an archive - through a bound, since a declared size is only a claim - and says what it is. Nothing is written anywhere
    /// and nothing is run.
    /// </summary>
    private static ZipSourceFinding InspectZipSource(ZipArchiveEntry entry, ref long budget)
    {
        var path = ZipMapping.NormalizePath(entry.FullName);
        var limit = Math.Min(MaxZipSourceBytes, budget);
        if (entry.Length > limit)
        {
            return new ZipSourceFinding(path, null, null, null, "too large to check");
        }

        byte[] bytes;
        using (var stream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[16384];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit)
                {
                    budget = 0;
                    return new ZipSourceFinding(path, null, null, null, "larger than it said it was, so it was not read");
                }
            }

            bytes = buffer.ToArray();
        }

        budget -= bytes.Length;
        if (!TryReadText(bytes, out var source))
        {
            return new ZipSourceFinding(path, null, null, null, "not text");
        }

        var problems = PluginSourceSyntax.Problems(source, LanguageVersion.Latest, out var total);
        if (problems.Count > 0)
        {
            var extra = total > 1 ? $" (and {total - 1} more)" : string.Empty;
            return new ZipSourceFinding(path, null, null, null, Trim($"does not read as C#: {problems[0]}{extra}", 200));
        }

        var info = FindPluginInfo(source);
        return info is null
            ? new ZipSourceFinding(path, null, null, null, null)
            : new ZipSourceFinding(path, Trim(info.Value.Class, 100), Trim(info.Value.Name, 200), Trim(info.Value.Version, 50), null);
    }

    private static bool IsUnsafeEntryName(string name) =>
        name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':') || name.Contains('\0')
        || name.Split('/', '\\').Any(part => part == "..");

    private static bool LooksLikeHtml(string source)
    {
        var head = source.AsSpan(0, Math.Min(source.Length, 1024)).TrimStart();
        return head.StartsWith("<", StringComparison.Ordinal)
            && (head.Contains("<html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The class name and the <c>[Info(name, author, version)]</c> of the first plugin class in the file, or null if there is none.</summary>
    private static (string Class, string Name, string Author, string Version)? FindPluginInfo(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var extendsPlugin = cls.BaseList?.Types.Any(t => PluginBases.Contains(LastName(t.Type.ToString()))) ?? false;
            if (!extendsPlugin)
            {
                continue;
            }

            var attribute = cls.AttributeLists.SelectMany(l => l.Attributes)
                .FirstOrDefault(a => LastName(a.Name.ToString()) is "Info" or "InfoAttribute");
            var args = attribute?.ArgumentList?.Arguments;
            if (args is null || args.Value.Count < 3)
            {
                continue;
            }

            return (cls.Identifier.ValueText, Literal(args.Value[0]), Literal(args.Value[1]), Literal(args.Value[2]));
        }

        return null;
    }

    // "Oxide.Plugins.RustPlugin" and "RustPlugin" are the same base.
    private static string LastName(string dotted) => dotted[(dotted.LastIndexOf('.') + 1)..].Trim();

    // A string or a number written into the attribute; anything computed is not something a listing can say, so it reads as empty.
    private static string Literal(AttributeArgumentSyntax argument) =>
        argument.Expression is LiteralExpressionSyntax literal ? literal.Token.ValueText : string.Empty;

    private static string Letters(string text) => PluginUpdateNoticeRepository.Normalize(text);

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
