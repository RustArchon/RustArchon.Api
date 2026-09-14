// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustArchon.Api.Administration;

/// <summary>One validated, fully-read file from an accepted theme package.</summary>
public record ThemePackageEntry(string Path, string ContentType, byte[] Content);

/// <summary>
/// A theme package's own declared identity, parsed from its <c>manifest.json</c> - see
/// <see cref="ThemePackageValidator.ParseManifest"/> for every validation rule each field is held to.
/// This, not an admin-typed value, is what <c>ThemeService.UploadAsync</c> copies onto the saved
/// <c>Theme</c> row: a distributable package is meant to describe itself.
/// </summary>
public record ThemeManifest(
    string Name, string Version, string? Description,
    string? AuthorName, string? AuthorEmail, string? Website, string? UpdateUrl);

/// <summary>The outcome of <see cref="ThemePackageValidator.Validate"/> - either every entry in
/// <see cref="Entries"/> is ready to upload and <see cref="Manifest"/> is populated, or
/// <see cref="Errors"/> explains every reason it wasn't.</summary>
public record ThemeValidationResult(
    bool Success, IReadOnlyList<string> Errors, IReadOnlyList<ThemePackageEntry> Entries, ThemeManifest? Manifest)
{
    public static ThemeValidationResult Failed(IReadOnlyList<string> errors) => new(false, errors, [], null);

    public static ThemeValidationResult Succeeded(IReadOnlyList<ThemePackageEntry> entries, ThemeManifest manifest) =>
        new(true, [], entries, manifest);
}

/// <summary>
/// Validates an uploaded theme package (a zip file) before <c>ThemeService</c> ever writes a single
/// byte to object storage or Postgres - a pure, I/O-free-beyond-the-already-open-archive function,
/// deliberately kept separate from <c>ThemeService</c> so this security-critical logic can be tested in
/// isolation with hand-built zip fixtures, the same way <c>EmailTemplateRenderer</c> is tested apart
/// from <c>CommunicationPublisher</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The zip-slip guard is the reason this class exists.</strong> Extracting an untrusted zip
/// entry-by-entry into a target namespace (here, object storage keys under <c>themes/{id}/</c>) without
/// validating each entry's own path first is a well-known vulnerability class: an entry named
/// <c>"../../../etc/passwd"</c> (or, for this Api, something that would let one theme's upload
/// overwrite another theme's objects, or a key outside the <c>themes/</c> prefix entirely) escapes the
/// intended destination the moment it's naively joined onto a base path. Every entry's path is
/// normalized and rejected outright on any <c>..</c> segment, absolute path, or backslash - see
/// <see cref="TryNormalizePath"/>.
/// </para>
/// <para>
/// <strong>Also guards against a zip bomb</strong> by never trusting <see cref="ZipArchiveEntry.Length"/>
/// (the declared, attacker-controlled uncompressed size in the zip's own central directory) as the sole
/// size check - <see cref="CopyWithLimit"/> enforces the same per-file cap against the bytes actually
/// decompressed, regardless of what the header claimed.
/// </para>
/// <para>
/// No JS allowed, by deliberate product decision (not just an initial gap): the file-extension allowlist
/// below is exhaustive, not a denylist, so a new file type is only ever accepted by a code change here,
/// never by omission.
/// </para>
/// <para>
/// <strong>An allowed extension isn't enough on its own</strong> - <see cref="IsAllowedLocation"/>
/// also confines each one to the one place it's actually allowed to live (an extra stray <c>.css</c>
/// file, a <c>.png</c> three folders deep, a font sitting in <c>images/</c>) rather than accepting
/// anything the zip-slip guard alone would let through. The zip-slip guard answers "can this entry
/// escape the theme's own object-storage prefix?" (no); this answers "does this entry belong where it
/// is?" - a different question, and both need answering.
/// </para>
/// </remarks>
public static class ThemePackageValidator
{
    public const string ManifestFileName = "manifest.json";
    public const string StylesheetFileName = "theme.css";

    private const long MaxFileBytes = 8 * 1024 * 1024;
    private const long MaxTotalBytes = 20 * 1024 * 1024;
    private const int MaxEntryCount = 200;

    /// <summary>
    /// The complete, deliberately exhaustive set of file types a theme package may contain - CSS, the
    /// manifest, images, and fonts. No JS, no HTML, nothing else: see this class's own remarks.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ContentTypesByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".svg"] = "image/svg+xml",
            [".webp"] = "image/webp",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf"
        };

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp" };

    private static readonly HashSet<string> FontExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".woff", ".woff2", ".ttf", ".otf" };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Validates every entry in <paramref name="archive"/> and, only if every single one passes, reads
    /// them all fully into memory (themes are small - see <c>ObjectContent</c>'s own reasoning) ready
    /// for <c>ThemeService</c> to upload as-is. Requires <see cref="ManifestFileName"/> and
    /// <see cref="StylesheetFileName"/> at the package root - a zip that wraps everything in its own
    /// top-level folder is rejected with a clear error rather than guessed at.
    /// </summary>
    public static ThemeValidationResult Validate(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var fileEntries = archive.Entries.Where(e => !(e.FullName.EndsWith('/') && e.Length == 0)).ToList();

        if (fileEntries.Count == 0)
        {
            return ThemeValidationResult.Failed(["The package is empty."]);
        }

        if (fileEntries.Count > MaxEntryCount)
        {
            return ThemeValidationResult.Failed([$"The package contains more than {MaxEntryCount} files."]);
        }

        var errors = new List<string>();
        var entries = new List<ThemePackageEntry>();
        long totalBytes = 0;

        foreach (var entry in fileEntries)
        {
            if (!TryNormalizePath(entry.FullName, out var normalizedPath, out var pathError))
            {
                errors.Add($"'{entry.FullName}': {pathError}");
                continue;
            }

            var extension = Path.GetExtension(normalizedPath);
            if (!ContentTypesByExtension.TryGetValue(extension, out var contentType))
            {
                errors.Add($"'{normalizedPath}': file type '{extension}' isn't allowed in a theme package.");
                continue;
            }

            if (!IsAllowedLocation(normalizedPath, extension, out var locationError))
            {
                errors.Add(locationError!);
                continue;
            }

            if (errors.Count > 0)
            {
                // Already broken - no reason to also decompress this entry's bytes below. Every entry
                // still gets its own path/extension check above so one bad file's error doesn't hide a
                // second, unrelated one, but nothing gets read into memory once the package is known to
                // fail regardless.
                continue;
            }

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();

            if (!CopyWithLimit(entryStream, buffer, MaxFileBytes))
            {
                errors.Add($"'{normalizedPath}' exceeds the {MaxFileBytes / (1024 * 1024)} MB per-file limit.");
                continue;
            }

            totalBytes += buffer.Length;
            if (totalBytes > MaxTotalBytes)
            {
                return ThemeValidationResult.Failed(
                    [$"The package's total size exceeds the {MaxTotalBytes / (1024 * 1024)} MB limit."]);
            }

            entries.Add(new ThemePackageEntry(normalizedPath, contentType, buffer.ToArray()));
        }

        if (errors.Count > 0)
        {
            return ThemeValidationResult.Failed(errors);
        }

        var manifestEntry = entries.FirstOrDefault(e => e.Path == ManifestFileName);
        if (manifestEntry is null)
        {
            errors.Add($"The package is missing a required '{ManifestFileName}' file at its root.");
        }

        if (!entries.Any(e => e.Path == StylesheetFileName))
        {
            errors.Add($"The package is missing a required '{StylesheetFileName}' file at its root.");
        }

        if (errors.Count > 0)
        {
            return ThemeValidationResult.Failed(errors);
        }

        if (!TryParseManifest(manifestEntry!.Content, out var manifest, out var manifestErrors))
        {
            return ThemeValidationResult.Failed(manifestErrors);
        }

        return ThemeValidationResult.Succeeded(entries, manifest);
    }

    /// <summary>The literal JSON shape <c>manifest.json</c> is deserialized against - every property
    /// nullable regardless of <see cref="ThemeManifest"/>'s own requiredness, so a missing or malformed
    /// field is this method's own error to report rather than a deserialization exception. Internal
    /// rather than private so <see cref="ThemePackageBuilder"/> can serialize this exact same shape when
    /// assembling a manifest.json from the theme-builder UI's form fields, rather than a second,
    /// independently-maintained copy of it.</summary>
    internal sealed record RawManifest(
        string? Name, string? Version, string? Description,
        string? AuthorName, string? AuthorEmail, string? Website, string? UpdateUrl);

    // Permissive semver-shaped check (major[.minor[.patch]], optional -prerelease/+build metadata) -
    // not full SemVer grammar, since nothing here does more than basic ordering yet (see
    // ThemeVersionComparer); just enough to catch "that's not a version number" (e.g. a stray product
    // name) before it lands in the catalog, and to give the update-check something shaped consistently
    // enough to compare. Internal rather than private so ThemeVersionComparer can reuse this exact
    // pattern instead of drifting from a second copy of it.
    internal static readonly Regex VersionPattern =
        new(@"^\d+(\.\d+){0,2}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    /// <summary>Internal rather than private so <see cref="ThemePackageBuilder"/> serializes a
    /// <see cref="RawManifest"/> with the exact same camelCase JSON convention this class deserializes
    /// it with.</summary>
    internal static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Deserializes and validates <c>manifest.json</c>'s bytes - <see cref="ThemeManifest.Name"/> and
    /// <see cref="ThemeManifest.Version"/> are required (an empty/missing value for either fails the
    /// whole package, same as a missing <see cref="StylesheetFileName"/> does); every other field is
    /// optional but format-checked when present, since a garbled author email or update URL would only
    /// surface as a confusing failure much later (an admin's or a future update-check's own attempt to
    /// use it), not here where the actual problem is obvious and nameable.
    /// </summary>
    private static bool TryParseManifest(byte[] content, out ThemeManifest manifest, out List<string> errors)
    {
        errors = [];
        manifest = null!;

        RawManifest? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawManifest>(content, ManifestJsonOptions);
        }
        catch (JsonException ex)
        {
            errors.Add($"'{ManifestFileName}' isn't valid JSON: {ex.Message}");
            return false;
        }

        if (raw is null)
        {
            errors.Add($"'{ManifestFileName}' is empty.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw.Name))
        {
            errors.Add($"'{ManifestFileName}': 'name' is required.");
        }
        else if (raw.Name.Length > 200)
        {
            errors.Add($"'{ManifestFileName}': 'name' can't be longer than 200 characters.");
        }

        if (string.IsNullOrWhiteSpace(raw.Version))
        {
            errors.Add($"'{ManifestFileName}': 'version' is required.");
        }
        else if (!VersionPattern.IsMatch(raw.Version.Trim()))
        {
            errors.Add($"'{ManifestFileName}': 'version' ('{raw.Version}') doesn't look like a version number, e.g. 1.0.0.");
        }

        if (raw.Description is { Length: > 2000 })
        {
            errors.Add($"'{ManifestFileName}': 'description' can't be longer than 2000 characters.");
        }

        if (raw.AuthorName is { Length: > 200 })
        {
            errors.Add($"'{ManifestFileName}': 'authorName' can't be longer than 200 characters.");
        }

        // camelCase field names throughout - what a theme author actually typed in their own
        // manifest.json, not this class's internal C# property names.
        ValidateOptionalEmail(raw.AuthorEmail, "authorEmail", errors);
        ValidateOptionalUrl(raw.Website, "website", errors);
        ValidateOptionalUrl(raw.UpdateUrl, "updateUrl", errors);

        if (errors.Count > 0)
        {
            return false;
        }

        manifest = new ThemeManifest(
            raw.Name!.Trim(), raw.Version!.Trim(), Normalize(raw.Description),
            Normalize(raw.AuthorName), Normalize(raw.AuthorEmail), Normalize(raw.Website), Normalize(raw.UpdateUrl));
        return true;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateOptionalEmail(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > 320 || !MailAddressLooksValid(value))
        {
            errors.Add($"'{ManifestFileName}': '{fieldName}' ('{value}') isn't a valid email address.");
        }
    }

    private static bool MailAddressLooksValid(string value)
    {
        try
        {
            // .NET's own parser, not a hand-rolled regex - RFC 5322 addresses have enough edge cases
            // (quoted local parts, comments) that reimplementing the grammar here would just be a worse
            // copy of what the framework already gets right.
            _ = new System.Net.Mail.MailAddress(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateOptionalUrl(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > 2000
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"'{ManifestFileName}': '{fieldName}' ('{value}') must be a full http:// or https:// URL.");
        }
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> from <paramref name="source"/> to
    /// <paramref name="destination"/>, returning <c>false</c> the moment that limit would be exceeded -
    /// deliberately not <see cref="Stream.CopyTo(Stream)"/>, which trusts the caller to have already
    /// bounded things and would happily decompress an arbitrarily large payload behind a small declared
    /// <see cref="ZipArchiveEntry.Length"/>. See this class's own remarks on why the declared size alone
    /// isn't trusted.
    /// </summary>
    private static bool CopyWithLimit(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return false;
            }

            destination.Write(buffer, 0, read);
        }

        return true;
    }

    /// <summary>
    /// The zip-slip guard - see this class's own remarks. Rejects absolute paths, backslashes (a real
    /// zip's entry paths always use forward slashes per the ZIP spec, so a backslash here is itself a
    /// sign of a maliciously-crafted entry, not a legitimate Windows-authored one), and any <c>.</c>/<c>..</c>
    /// path segment.
    /// </summary>
    private static bool TryNormalizePath(string rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "empty entry name.";
            return false;
        }

        if (rawPath.Contains('\\'))
        {
            error = "contains a backslash, which is never valid in a zip entry's own path.";
            return false;
        }

        if (rawPath.StartsWith('/'))
        {
            error = "is an absolute path.";
            return false;
        }

        var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(s => s is ".." or "."))
        {
            error = "contains a '..' or '.' path segment.";
            return false;
        }

        // Not a traversal risk on the Linux hosts this stack actually deploys to, but a downloaded
        // package may well get extracted or inspected locally on Windows too (this repo's own primary
        // dev environment) - cheap to reject outright rather than assume every future consumer runs on
        // Linux.
        if (segments.Any(s => WindowsReservedNames.Contains(Path.GetFileNameWithoutExtension(s))))
        {
            error = "contains a path segment reserved on Windows.";
            return false;
        }

        normalizedPath = string.Join('/', segments);
        error = null;
        return true;
    }

    /// <summary>
    /// Confines every extension to the specific place it's actually allowed to live, not just an
    /// allowed extension anywhere the zip-slip guard permits - a passing <see cref="TryNormalizePath"/>
    /// only means an entry can't escape the theme's own object-storage prefix, it says nothing about
    /// whether the entry belongs where it is. <c>manifest.json</c> and <c>theme.css</c> are singular
    /// (that exact name, at the root, and nothing else may claim that extension anywhere in the
    /// package); images and fonts may sit at the root or one level under <c>images/</c>/<c>fonts/</c>
    /// respectively, never nested deeper and never in the other category's folder.
    /// </summary>
    private static bool IsAllowedLocation(string normalizedPath, string extension, out string? error)
    {
        var segments = normalizedPath.Split('/');

        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedPath == ManifestFileName)
            {
                error = null;
                return true;
            }

            error = $"'{normalizedPath}': only '{ManifestFileName}', at the package root, may be a .json file.";
            return false;
        }

        if (string.Equals(extension, ".css", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedPath == StylesheetFileName)
            {
                error = null;
                return true;
            }

            error = $"'{normalizedPath}': only '{StylesheetFileName}', at the package root, may be a .css file.";
            return false;
        }

        if (ImageExtensions.Contains(extension))
        {
            if (segments.Length == 1 || (segments.Length == 2 && segments[0] == "images"))
            {
                error = null;
                return true;
            }

            error = $"'{normalizedPath}': images must be at the package root or directly under 'images/', not nested any further.";
            return false;
        }

        if (FontExtensions.Contains(extension))
        {
            if (segments.Length == 1 || (segments.Length == 2 && segments[0] == "fonts"))
            {
                error = null;
                return true;
            }

            error = $"'{normalizedPath}': fonts must be at the package root or directly under 'fonts/', not nested any further.";
            return false;
        }

        // Unreachable given the caller only reaches here for an extension ContentTypesByExtension
        // already accepted, and every one of those is handled above - kept as a loud failure rather
        // than a silent pass-through in case the two ever drift out of sync.
        throw new InvalidOperationException($"'{extension}' has no known location rule - update {nameof(IsAllowedLocation)} alongside {nameof(ContentTypesByExtension)}.");
    }
}
