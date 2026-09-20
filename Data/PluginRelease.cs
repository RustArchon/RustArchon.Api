// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>Which of the two plugin files a release is.</summary>
public enum PluginReleaseKind
{
    /// <summary><c>RustArchon.cs</c>, the plugin itself.</summary>
    Main = 1,

    /// <summary><c>RustArchonUpdater.cs</c>, the small plugin that applies updates. Only ever installed by hand.</summary>
    Updater = 2
}

/// <summary>Whether a release is being served.</summary>
public enum PluginReleaseState
{
    /// <summary>Uploaded and validated, but not served. Nothing reaches a game server from a draft.</summary>
    Draft = 0,

    /// <summary>Eligible to be served: the highest published version of its kind is what the Panel offers.</summary>
    Published = 1,

    /// <summary>Taken out of service (it turned out to be bad). Kept, never deleted.</summary>
    Withdrawn = 2
}

/// <summary>
/// A plugin source file a platform admin uploaded for delivery. Without one, the Panel serves the plugin source
/// embedded in the Api build; the highest <see cref="PluginReleaseState.Published"/> release of a kind that is newer
/// than the embedded version replaces it, so a plugin fix ships without shipping the Api.
/// </summary>
/// <remarks>
/// The stored text is the <b>unstamped, unsigned</b> source: the Panel stamps its key in and signs it at serve time,
/// exactly as it does the embedded source. The upload is refused if it already carries a signature line or a stamped
/// key, so what was reviewed is what gets signed. Immutable once stored: to change it, upload a new version.
/// </remarks>
[Table("PluginRelease")]
[Index(nameof(Kind), nameof(Version), IsUnique = true, Name = "IX_PluginRelease_Kind_Version")]
public class PluginRelease : Entity
{
    public PluginReleaseKind Kind { get; set; }

    /// <summary>The <c>[Info]</c> version, major.minor.patch.</summary>
    [Required]
    [MaxLength(20)]
    public string Version { get; set; } = string.Empty;

    /// <summary>The source exactly as uploaded (LF-normalized, no byte-order mark).</summary>
    [Required]
    public string SourceText { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of <see cref="SourceText"/>'s UTF-8 bytes, so a release can be told apart at a glance.</summary>
    [Required]
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public PluginReleaseState State { get; set; } = PluginReleaseState.Draft;

    public DateTimeOffset UploadedAtUtc { get; set; }

    [Required]
    [MaxLength(200)]
    public string UploadedBy { get; set; } = string.Empty;

    public DateTimeOffset? PublishedAtUtc { get; set; }

    [MaxLength(200)]
    public string? PublishedBy { get; set; }

    public DateTimeOffset? WithdrawnAtUtc { get; set; }

    [MaxLength(200)]
    public string? WithdrawnBy { get; set; }

    [MaxLength(500)]
    public string? WithdrawnReason { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }
}
