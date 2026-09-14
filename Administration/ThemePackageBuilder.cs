// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace RustArchon.Api.Administration;

/// <summary>
/// Everything the theme-builder UI collects to assemble a package - the same information a
/// hand-authored .zip's <c>manifest.json</c>/<c>theme.css</c>/images/fonts would carry, just gathered as
/// individual form fields and file uploads instead of a pre-made archive. <see cref="Name"/> and
/// <see cref="Version"/> are required the same way they are in a real manifest; every other field is
/// optional, and <see cref="Images"/>/<see cref="Fonts"/> may both be empty (a theme need not use either).
/// </summary>
public class ThemeBuildRequest
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorEmail { get; set; }
    public string? Website { get; set; }
    public string? UpdateUrl { get; set; }

    /// <summary>The <c>theme.css</c> content, verbatim - whatever the builder's CSS editor holds when
    /// the admin publishes.</summary>
    public string Css { get; set; } = string.Empty;

    public List<IFormFile>? Images { get; set; }
    public List<IFormFile>? Fonts { get; set; }
}

/// <summary>The outcome of <see cref="ThemePackageBuilder.Build"/>.</summary>
public record ThemePackageBuildResult(bool Success, MemoryStream? Package, IReadOnlyList<string> Errors)
{
    public static ThemePackageBuildResult Failed(IReadOnlyList<string> errors) => new(false, null, errors);

    public static ThemePackageBuildResult Succeeded(MemoryStream package) => new(true, package, []);
}

/// <summary>
/// Assembles a <see cref="ThemeBuildRequest"/> - the theme-builder UI's form fields and file uploads -
/// into the exact same .zip shape a hand-authored theme package would be: <c>manifest.json</c>,
/// <c>theme.css</c>, <c>images/*</c>, <c>fonts/*</c>, and nothing more.
/// </summary>
/// <remarks>
/// Deliberately does none of the actual content validation itself - no required-field check, no version
/// format check, no extension allowlist, no size limit. The assembled package is handed to
/// <see cref="ThemeService.UploadAsync"/> exactly like a manual admin upload would be, so every rule
/// <see cref="ThemePackageValidator"/> already enforces applies automatically, through the one path that
/// already implements them - a theme built in the Panel is validated identically to one uploaded as a
/// .zip, rather than against a second, independently-maintained copy of those rules. The one thing this
/// class does have to catch itself is a collision <see cref="ThemePackageValidator"/> could never
/// encounter from a real zip: two files in the same category sharing a sanitized name, which would
/// otherwise silently collide into a single zip entry.
/// </remarks>
public static class ThemePackageBuilder
{
    public static ThemePackageBuildResult Build(ThemeBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        CheckForNameCollisions(request.Images, "image", errors);
        CheckForNameCollisions(request.Fonts, "font", errors);

        if (errors.Count > 0)
        {
            return ThemePackageBuildResult.Failed(errors);
        }

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddManifest(archive, request);
            AddText(archive, ThemePackageValidator.StylesheetFileName, request.Css);

            foreach (var image in request.Images ?? [])
            {
                AddFile(archive, $"images/{SanitizedFileName(image.FileName)}", image);
            }

            foreach (var font in request.Fonts ?? [])
            {
                AddFile(archive, $"fonts/{SanitizedFileName(font.FileName)}", font);
            }
        }

        stream.Position = 0;
        return ThemePackageBuildResult.Succeeded(stream);
    }

    /// <summary>
    /// Rejects the whole request (writing nothing) if any two files in <paramref name="files"/> would
    /// sanitize to the same zip entry name, or if any file has no usable name at all - both caller
    /// mistakes worth a clear message rather than one file silently overwriting another inside the
    /// assembled zip.
    /// </summary>
    private static void CheckForNameCollisions(List<IFormFile>? files, string category, List<string> errors)
    {
        if (files is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var name = SanitizedFileName(file.FileName);

            if (string.IsNullOrEmpty(name))
            {
                errors.Add($"An {category} was uploaded with no usable file name.");
            }
            else if (!seen.Add(name))
            {
                errors.Add($"Two {category}s are both named '{name}' - rename one of them.");
            }
        }
    }

    /// <summary>
    /// Strips any directory component from a browser-supplied file name before it becomes a zip entry
    /// path - defense in depth against a crafted <c>Content-Disposition</c> header, the same reasoning
    /// <see cref="ThemePackageValidator"/>'s own zip-slip guard exists for, just applied one step
    /// earlier: a path this builder never lets happen, rather than one a validator has to detect and
    /// reject afterward.
    /// </summary>
    private static string SanitizedFileName(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileName(fileName.Replace('\\', '/'));

    /// <summary>Builds <c>manifest.json</c> from the request's own fields, using the exact same
    /// <see cref="ThemePackageValidator.RawManifest"/> shape and JSON convention
    /// <see cref="ThemePackageValidator.TryParseManifest"/> deserializes against - so whatever the admin
    /// left blank or malformed here is caught by that same method, moments later, with the same
    /// messages a hand-authored manifest.json's mistakes would get.</summary>
    private static void AddManifest(ZipArchive archive, ThemeBuildRequest request)
    {
        var raw = new ThemePackageValidator.RawManifest(
            request.Name, request.Version, request.Description,
            request.AuthorName, request.AuthorEmail, request.Website, request.UpdateUrl);

        var json = JsonSerializer.Serialize(raw, ThemePackageValidator.ManifestJsonOptions);
        AddText(archive, ThemePackageValidator.ManifestFileName, json);
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        using var entryStream = archive.CreateEntry(entryName).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        entryStream.Write(bytes);
    }

    private static void AddFile(ZipArchive archive, string entryName, IFormFile file)
    {
        using var entryStream = archive.CreateEntry(entryName).Open();
        using var sourceStream = file.OpenReadStream();
        sourceStream.CopyTo(entryStream);
    }
}
