// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>A refused release action. <see cref="Code"/> is stable and safe to show.</summary>
public sealed class PluginReleaseException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One release as the admin page shows it (never the source text - that is fetched only to be signed).</summary>
public sealed record PluginReleaseInfo(
    Guid Id,
    PluginReleaseKind Kind,
    string Version,
    PluginReleaseState State,
    string Sha256,
    DateTimeOffset UploadedAtUtc,
    string UploadedBy,
    DateTimeOffset? PublishedAtUtc,
    string? PublishedBy,
    DateTimeOffset? WithdrawnAtUtc,
    string? WithdrawnBy,
    string? WithdrawnReason,
    string? Notes,
    bool IsServed);

/// <summary>Which source the Panel serves for one plugin file, and where it came from.</summary>
public sealed record PluginServedSource(string Text, string? Version, bool FromRelease, Guid? ReleaseId);

/// <summary>Uploading, publishing and withdrawing plugin releases; and deciding which source the Panel serves.</summary>
public interface IPluginReleaseService
{
    Task<IReadOnlyList<PluginReleaseInfo>> ListAsync();

    /// <summary>
    /// Validates and stores an uploaded plugin file as a <see cref="PluginReleaseState.Draft"/>: nothing is served from
    /// it until it is published.
    /// </summary>
    /// <exception cref="PluginReleaseException">See <see cref="PluginReleaseService"/> for the codes.</exception>
    Task<PluginReleaseInfo> UploadAsync(PluginReleaseKind kind, byte[] content, string actor, string? notes);

    /// <summary>Makes a draft eligible to be served (the highest published version of a kind is what is offered).</summary>
    Task<PluginReleaseInfo> PublishAsync(Guid id, string actor);

    /// <summary>Takes a draft or published release out of service for good. Never deletes it.</summary>
    Task<PluginReleaseInfo> WithdrawAsync(Guid id, string reason, string actor);

    /// <summary>The source to stamp, sign and serve for this kind: the newest published release, else the embedded build.</summary>
    Task<PluginServedSource> ResolveAsync(PluginReleaseKind kind);

    /// <summary>The version embedded in this Api build for this kind - what is served when no release is newer.</summary>
    string? EmbeddedVersion(PluginReleaseKind kind);
}

/// <inheritdoc cref="IPluginReleaseService" />
/// <remarks>
/// <para>
/// This is the most sensitive action on the page: what is uploaded here is signed by this Panel and pushed to game
/// servers that run it with full privileges. So an upload is only ever a draft, must pass every check below, is
/// immutable once stored, and needs a separate explicit publish; each step is audit-logged.
/// </para>
/// <para>
/// Upload refusals: <c>empty</c>, <c>too_large</c> (over 512 KiB), <c>not_text</c> (not valid UTF-8),
/// <c>wrong_plugin</c> (the <c>[Info]</c> title is not the expected one for the chosen kind), <c>no_version</c>,
/// <c>bad_placeholders</c> (the two key placeholders must each appear exactly once), <c>already_signed</c> (the file
/// already carries a signature line - it must be the raw source, so what was reviewed is what gets signed),
/// <c>version_exists</c>.
/// </para>
/// </remarks>
public partial class PluginReleaseService(
    ApiDbContext context,
    EmbeddedPluginScriptSource embedded,
    TimeProvider clock,
    ILogger<PluginReleaseService> logger) : IPluginReleaseService
{
    public const int MaxBytes = 512 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string? EmbeddedVersion(PluginReleaseKind kind) => ReadVersion(EmbeddedText(kind), TitleFor(kind));

    public async Task<IReadOnlyList<PluginReleaseInfo>> ListAsync()
    {
        var all = await context.PluginReleases.AsNoTracking().ToListAsync();
        var served = new Dictionary<PluginReleaseKind, Guid?>
        {
            [PluginReleaseKind.Main] = (await ResolveAsync(PluginReleaseKind.Main)).ReleaseId,
            [PluginReleaseKind.Updater] = (await ResolveAsync(PluginReleaseKind.Updater)).ReleaseId
        };

        return all
            .OrderBy(r => r.Kind)
            .ThenByDescending(r => r.UploadedAtUtc)
            .Select(r => Info(r, served[r.Kind] == r.Id))
            .ToList();
    }

    public async Task<PluginServedSource> ResolveAsync(PluginReleaseKind kind)
    {
        var embeddedText = EmbeddedText(kind);
        var embeddedVersion = ReadVersion(embeddedText, TitleFor(kind));

        var published = await context.PluginReleases.AsNoTracking()
            .Where(r => r.Kind == kind && r.State == PluginReleaseState.Published)
            .ToListAsync();

        // Highest published version; ties cannot happen (kind + version is unique).
        PluginRelease? best = null;
        foreach (var release in published)
        {
            if (best is null || PluginVersions.IsNewer(release.Version, best.Version))
            {
                best = release;
            }
        }

        // A release only replaces the embedded build when it is strictly newer than it: an old release must not
        // quietly roll every server back to it, and equal is the same code.
        if (best is not null && (embeddedVersion is null || PluginVersions.IsNewer(best.Version, embeddedVersion)))
        {
            return new PluginServedSource(best.SourceText, best.Version, FromRelease: true, best.Id);
        }

        return new PluginServedSource(embeddedText, embeddedVersion, FromRelease: false, null);
    }

    public async Task<PluginReleaseInfo> UploadAsync(PluginReleaseKind kind, byte[] content, string actor, string? notes)
    {
        if (content is null || content.Length == 0)
        {
            throw new PluginReleaseException("empty", "The file is empty.");
        }

        if (content.Length > MaxBytes)
        {
            throw new PluginReleaseException("too_large", $"The file is over {MaxBytes / 1024} KiB.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new PluginReleaseException("not_text", "The file is not valid UTF-8 text.");
        }

        // Same normalization the stamper applies, so what is stored is what will be signed.
        text = text.TrimStart('﻿').Replace("\r\n", "\n");
        if (!text.EndsWith('\n'))
        {
            text += "\n";
        }

        var title = TitleFor(kind);
        if (!Regex.IsMatch(text, @"^\s*\[Info\(""" + Regex.Escape(title) + @"""\s*,", RegexOptions.Multiline))
        {
            throw new PluginReleaseException("wrong_plugin", $"This is not the {title} plugin: its [Info] title must be \"{title}\".");
        }

        var version = ReadVersion(text, title);
        if (version is null || !PluginVersions.IsValid(version))
        {
            throw new PluginReleaseException("no_version", "The [Info] attribute has no major.minor.patch version.");
        }

        try
        {
            PluginScriptStamper.Stamp(text, "x", "x"); // only to check the placeholders; the result is discarded
        }
        catch (InvalidOperationException)
        {
            throw new PluginReleaseException(
                "bad_placeholders",
                "The two key placeholders (@@RUSTARCHON_TRUSTED_MODULUS@@ and @@RUSTARCHON_TRUSTED_EXPONENT@@) must each appear exactly once. Upload the raw source, not a file downloaded from a Panel.");
        }

        if (text.Split('\n').Any(l => l.StartsWith(PluginScriptStamper.SignatureMarker, StringComparison.Ordinal)))
        {
            throw new PluginReleaseException(
                "already_signed",
                "The file already carries a signature line. Upload the raw source; the Panel signs it itself.");
        }

        if (await context.PluginReleases.AnyAsync(r => r.Kind == kind && r.Version == version))
        {
            throw new PluginReleaseException("version_exists", $"Version {version} of the {title} plugin has already been uploaded. Releases cannot be changed: upload a new version.");
        }

        var now = clock.GetUtcNow();
        var release = new PluginRelease
        {
            Kind = kind,
            Version = version,
            SourceText = text,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            State = PluginReleaseState.Draft,
            UploadedAtUtc = now,
            UploadedBy = actor,
            Notes = Trim(notes, 500)
        };
        context.PluginReleases.Add(release);
        context.PluginAdminEvents.Add(Event(PluginAdminEventKind.ReleaseUploaded, kind, version, actor, now, $"sha256 {release.Sha256[..12]}, {content.Length} bytes"));
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            throw new PluginReleaseException("version_exists", $"Version {version} of the {title} plugin has already been uploaded.");
        }

        logger.LogWarning("Plugin release {Kind} {Version} uploaded by {Actor} (draft).", kind, version, actor);
        return Info(release, false);
    }

    public async Task<PluginReleaseInfo> PublishAsync(Guid id, string actor)
    {
        var release = await context.PluginReleases.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new PluginReleaseException("not_found", "No such release.");
        if (release.State != PluginReleaseState.Draft)
        {
            throw new PluginReleaseException("not_a_draft", release.State == PluginReleaseState.Published ? "That release is already published." : "A withdrawn release cannot be published. Upload a new version.");
        }

        var now = clock.GetUtcNow();
        release.State = PluginReleaseState.Published;
        release.PublishedAtUtc = now;
        release.PublishedBy = actor;
        context.PluginAdminEvents.Add(Event(PluginAdminEventKind.ReleasePublished, release.Kind, release.Version, actor, now, null));
        await context.SaveChangesAsync();

        logger.LogWarning("Plugin release {Kind} {Version} published by {Actor}.", release.Kind, release.Version, actor);
        var served = (await ResolveAsync(release.Kind)).ReleaseId == release.Id;
        return Info(release, served);
    }

    public async Task<PluginReleaseInfo> WithdrawAsync(Guid id, string reason, string actor)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new PluginReleaseException("reason_required", "Withdrawing a release needs a reason.");
        }

        var release = await context.PluginReleases.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new PluginReleaseException("not_found", "No such release.");
        if (release.State == PluginReleaseState.Withdrawn)
        {
            throw new PluginReleaseException("already_withdrawn", "That release is already withdrawn.");
        }

        var now = clock.GetUtcNow();
        release.State = PluginReleaseState.Withdrawn;
        release.WithdrawnAtUtc = now;
        release.WithdrawnBy = actor;
        release.WithdrawnReason = Trim(reason.Trim(), 500);
        context.PluginAdminEvents.Add(Event(PluginAdminEventKind.ReleaseWithdrawn, release.Kind, release.Version, actor, now, Trim(reason, 900)));
        await context.SaveChangesAsync();

        logger.LogWarning("Plugin release {Kind} {Version} withdrawn by {Actor}: {Reason}", release.Kind, release.Version, actor, reason);
        return Info(release, false);
    }

    private static PluginAdminEvent Event(PluginAdminEventKind kind, PluginReleaseKind releaseKind, string version, string actor, DateTimeOffset at, string? detail) =>
        new() { AtUtc = at, Kind = kind, Subject = $"{releaseKind} {version}", Actor = actor, Detail = detail };

    private static PluginReleaseInfo Info(PluginRelease r, bool served) => new(
        r.Id, r.Kind, r.Version, r.State, r.Sha256, r.UploadedAtUtc, r.UploadedBy, r.PublishedAtUtc, r.PublishedBy,
        r.WithdrawnAtUtc, r.WithdrawnBy, r.WithdrawnReason, r.Notes, served);

    private string EmbeddedText(PluginReleaseKind kind) =>
        kind == PluginReleaseKind.Main ? embedded.ReadSource() : embedded.ReadUpdaterSource();

    private static string TitleFor(PluginReleaseKind kind) => kind == PluginReleaseKind.Main ? "RustArchon" : "RustArchonUpdater";

    private static string? Trim(string? text, int max) =>
        string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max];

    // The [Info("<title>", "<author>", "x.y.z")] attribute's third argument.
    private static string? ReadVersion(string text, string title)
    {
        // Anchored to the start of a line: a comment that merely mentions an [Info] attribute (the Updater's does) is not one.
        var match = Regex.Match(text, @"^\s*\[Info\(""" + Regex.Escape(title) + @"""\s*,\s*""[^""]*""\s*,\s*""([^""]+)""\)\]", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// The source the Panel stamps, signs and serves: the newest published release of each plugin file, else the build
/// embedded in this Api.
/// </summary>
public class PublishedPluginScriptSource(IPluginReleaseService releases) : IPluginScriptSource
{
    public async Task<string> ReadSourceAsync() => (await releases.ResolveAsync(PluginReleaseKind.Main)).Text;

    public async Task<string> ReadUpdaterSourceAsync() => (await releases.ResolveAsync(PluginReleaseKind.Updater)).Text;
}
