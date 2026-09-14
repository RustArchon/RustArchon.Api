// Copyright ©2026 Scott Blomfield

using System.ComponentModel.DataAnnotations;
using JumpStart.Data.Auditing;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// A platform-wide (not tenant-scoped) uploaded theme package - the durable catalog row pointing at a
/// file tree actually stored in <see cref="Infrastructure.ObjectStorage.IObjectStorage"/> under
/// <c>themes/{Id}/</c> (see <c>ThemeService</c>, the only place that prefix is built). Mirrors
/// <c>PlatformSetting</c>'s "not tenant-scoped" reasoning - a theme applies platform-wide, to every
/// tenant, same as branding.
/// </summary>
/// <remarks>
/// Holds catalog metadata only, never the package's actual bytes - same "Postgres owns the metadata,
/// object storage owns the content" split <c>Communication</c>/<c>HtmlBody</c> uses, just with the
/// content living in object storage here instead of inline in this table (a theme package, unlike an
/// email body, is a small file tree with binary assets, not one string).
/// </remarks>
public class Theme : AuditableEntity
{
    /// <summary>
    /// From the package's own <c>manifest.json</c> - not an admin-typed value. A theme package is
    /// meant to be a self-describing, distributable artifact (the original "packageable, self-contained
    /// themes people can distribute" brief), so its identity has to live in the package itself, not in
    /// a form field disconnected from it. See <c>ThemePackageValidator.ThemeManifest</c>, the only place
    /// this is parsed from.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// From the manifest - required there (unlike every other manifest field below) specifically so a
    /// future update-check against <see cref="UpdateUrl"/> has something reliable to compare against.
    /// Stored as the manifest's own literal string, not parsed into a structured version type - nothing
    /// here does version arithmetic yet, only equality/staleness comparisons an update check would do.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Version { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? AuthorName { get; set; }

    [MaxLength(320)]
    public string? AuthorEmail { get; set; }

    /// <summary>The author's own site - purely informational, shown to an admin browsing the catalog.
    /// Never fetched by this Api (see <see cref="UpdateUrl"/> for the one manifest URL that eventually
    /// will be).</summary>
    [MaxLength(2000)]
    public string? Website { get; set; }

    /// <summary>
    /// Where an admin-triggered "Check for updates" looks for a newer <see cref="Version"/> of this
    /// theme - optional, and only meaningful alongside <see cref="Version"/> (also required, for exactly
    /// this reason). This field itself comes from the package's own manifest (untrusted, possibly
    /// third-party-authored content) - see
    /// <see cref="Infrastructure.ThemeUpdates.ThemeUpdateCheckClient"/>'s remarks for why the actual fetch
    /// is held to a deliberately SSRF-hardened design rather than a plain <c>HttpClient</c> call, and
    /// <c>Administration.ThemeService.CheckForUpdateAsync</c>'s remarks for why this is admin-triggered
    /// only, never an unattended background job.
    /// </summary>
    [MaxLength(2000)]
    public string? UpdateUrl { get; set; }

    /// <summary>
    /// The version <see cref="UpdateUrl"/> most recently reported, or <c>null</c> if no check has ever
    /// succeeded. Compared against <see cref="Version"/> (see <c>Administration.ThemeVersionComparer</c>)
    /// to decide whether an update is available - never overwritten by a <em>failed</em> check, so a
    /// transient outage at the author's end doesn't make an already-known update disappear from the
    /// admin's view.
    /// </summary>
    [MaxLength(50)]
    public string? LatestKnownVersion { get; set; }

    /// <summary>
    /// Where to download the package for <see cref="LatestKnownVersion"/> - what
    /// <c>Administration.ThemeService.InstallUpdateAsync</c> fetches when an admin confirms the upgrade.
    /// Optional even when <see cref="LatestKnownVersion"/> is set: an update feed that only reports a
    /// version number still lets an admin know one exists, it just can't be installed with one click.
    /// </summary>
    [MaxLength(2000)]
    public string? LatestDownloadPackageUrl { get; set; }

    /// <summary>When an update check most recently ran against <see cref="UpdateUrl"/>, whether it
    /// succeeded or failed - <c>null</c> if one has never run.</summary>
    public DateTimeOffset? LastUpdateCheckOn { get; set; }

    /// <summary>
    /// Why the most recent update check failed, or <c>null</c> if it last succeeded (or has never run).
    /// Always one of a small set of fixed, safe-to-display messages - never a raw exception message or
    /// any part of the remote response - see <see cref="Infrastructure.ThemeUpdates.ThemeUpdateCheckClient"/>'s
    /// remarks on why that boundary matters here specifically.
    /// </summary>
    [MaxLength(200)]
    public string? LastUpdateCheckError { get; set; }

    /// <summary>Total size in bytes of every file in the package combined - shown on the admin list,
    /// and cheap to have on hand there without summing object storage listings per row.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Every relative path this theme's package actually contains (e.g. <c>"theme.css"</c>,
    /// <c>"images/hero.png"</c>) - the exact set of objects stored under <c>themes/{Id}/</c> in object
    /// storage. Recorded here (rather than re-listing object storage on every read) so
    /// <c>ThemeService.DeleteAsync</c> and the asset-serving endpoint's 404-vs-fetch decision don't
    /// depend on object storage being reachable just to know what should exist.
    /// </summary>
    /// <summary>
    /// Stored as a native Postgres <c>text[]</c> column - Npgsql's EF Core provider maps a plain
    /// <c>string[]</c> to one automatically, no extra configuration needed.
    /// </summary>
    public string[] AssetPaths { get; set; } = [];

    /// <summary>
    /// Whether this is the platform's currently-active theme. Enforced as exclusive by
    /// <c>ThemeService.ActivateAsync</c> (clears every other row's flag in the same transaction), not by
    /// a database constraint - the same "application code owns the single-active-item invariant" choice
    /// already made for <c>Subscription</c>/<c>OrganizationInvitation</c> elsewhere in this Api.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>Whether this theme's content came from an uploaded/installed package or was assembled
    /// in the Panel's own theme-builder - see <see cref="ThemeSource"/>'s own remarks for why this
    /// distinction matters (editing an <see cref="ThemeSource.Uploaded"/> theme in the builder is
    /// customizing something this instance didn't author).</summary>
    public ThemeSource Source { get; set; } = ThemeSource.Uploaded;
}
