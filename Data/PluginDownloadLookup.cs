// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>What asking for a plugin's direct download address came to.</summary>
public enum PluginDownloadOutcome
{
    /// <summary>A listing matched and offered an address that passed every check.</summary>
    Found = 0,

    /// <summary>The index answered, but not with anything usable: no such listing, no direct download offered (paid or login-only
    /// plugins), older than the update, or an address we would not pass on.</summary>
    NotFound = 1,

    /// <summary>The index could not be asked or did not answer sensibly (rate limited, down, too big, not the shape expected).</summary>
    Failed = 2
}

/// <summary>Where the check of the downloaded file itself has got to. The file is downloaded to be looked at, never kept.</summary>
public enum PluginFileValidationState
{
    /// <summary>Not looked at yet.</summary>
    NotChecked = 0,

    /// <summary>A single plugin source file that reads cleanly, is the plugin that was asked for, and is not older than the update: something that can be applied.</summary>
    Valid = 1,

    /// <summary>Downloaded, and not something that can be applied (an HTML page, not C#, does not read as C# a game server can compile, wrong plugin, older than the update).</summary>
    Invalid = 2,

    /// <summary>A zip archive. It is never applied without a person saying which of its files go where, so nothing more is checked until they have.</summary>
    NeedsInstructions = 3,

    /// <summary>The file could not be fetched (or the address was no longer one we would fetch). Tried again later, backing off.</summary>
    Failed = 4
}

/// <summary>
/// What the marketplace index said about one plugin at one version - everything it said, as it said it, and what was made of it. Shared
/// by every server and organization: whether <c>RaidableBases 3.1.9</c> has a direct download is a fact about the plugin, not about
/// whoever runs it, so it is asked once however many servers need the answer. It is kept as reported (the whole response is stored), so
/// a wrong match can be understood, or re-decided, without asking again.
/// </summary>
/// <remarks>
/// Not tenant-scoped, by design: it holds only public marketplace information (a plugin's name, its marketplace, a version and an
/// address) and nothing about any server or player.
/// </remarks>
[Table("PluginDownloadLookup")]
[Index(nameof(MarketplaceKey), nameof(NormalizedName), nameof(Version), IsUnique = true, Name = "IX_PluginDownloadLookup_Key")]
[Index(nameof(NextAttemptUtc), Name = "IX_PluginDownloadLookup_NextAttemptUtc")]
public class PluginDownloadLookup : Entity
{
    public const int MaxResponseLength = 1_000_000;

    /// <summary>The marketplace, lower case letters and digits only (<c>Lone.Design</c> is <c>lonedesign</c>).</summary>
    [Required]
    [MaxLength(100)]
    public string MarketplaceKey { get; set; } = string.Empty;

    /// <summary>The plugin's name, matched the way UpdateChecker's names are matched to a server's plugin list (letters and digits, lower case).</summary>
    [Required]
    [MaxLength(100)]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>The newest version UpdateChecker reported when this was asked, without a leading <c>v</c>.</summary>
    [Required]
    [MaxLength(50)]
    public string Version { get; set; } = string.Empty;

    public PluginDownloadOutcome Outcome { get; set; }

    /// <summary>The direct address, checked (https, and on the marketplace's own host) before it was stored. <c>null</c> unless <see cref="Outcome"/> is <see cref="PluginDownloadOutcome.Found"/>.</summary>
    [MaxLength(500)]
    public string? DownloadUrl { get; set; }

    /// <summary>The listing that was chosen: its name, page and version. For finding out why an address is what it is.</summary>
    [MaxLength(200)]
    public string? MatchedName { get; set; }

    [MaxLength(500)]
    public string? MatchedPageUrl { get; set; }

    [MaxLength(50)]
    public string? MatchedVersion { get; set; }

    /// <summary>Why nothing was found (or why the ask failed), in a few words.</summary>
    [MaxLength(200)]
    public string? Reason { get; set; }

    /// <summary>The index's whole answer, untouched. Untrusted text from a third party: read, never acted on. Cut off (and <see cref="ResponseTruncated"/> set) rather than refused when it is over <see cref="MaxResponseLength"/>.</summary>
    public string? ResponseJson { get; set; }

    public bool ResponseTruncated { get; set; }

    /// <summary>The HTTP status the index answered with, or <c>null</c> when it did not answer at all.</summary>
    public int? HttpStatus { get; set; }

    public DateTimeOffset CheckedAtUtc { get; set; }

    /// <summary>When to ask again, or <c>null</c> when the answer is settled (found). Backs off the more often it has not worked.</summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }

    public int Attempts { get; set; }

    // ---- the file itself ------------------------------------------------------------------------------------------------------------
    // Looked at once per version (Scott, 2026-09-21): downloaded, hashed and checked, and only what was learned is kept - never the file, which
    // RustArchon may have no licence to redistribute. The plugin on each game server downloads its own copy and is held to the hash below.

    /// <summary>How far the check of the file has got. Only a <see cref="PluginDownloadOutcome.Found"/> row is ever checked.</summary>
    public PluginFileValidationState ValidationState { get; set; }

    /// <summary>Why the file is what <see cref="ValidationState"/> says, in a few words - for a person, and for finding out later.</summary>
    [MaxLength(300)]
    public string? ValidationReason { get; set; }

    /// <summary><c>cs</c> (one plugin source file), <c>zip</c>, or <c>other</c> - what the bytes turned out to be, whatever the address said.</summary>
    [MaxLength(10)]
    public string? FileKind { get; set; }

    /// <summary>SHA-256 of the file's bytes as last downloaded, lower-case hex. What a game server's own copy must match before it is applied.</summary>
    [MaxLength(64)]
    public string? FileSha256 { get; set; }

    public long? FileSizeBytes { get; set; }

    /// <summary>The SHA-256 the file had before it last changed under the same version number, if it ever did (Scott: bad practice, but possible).</summary>
    [MaxLength(64)]
    public string? PreviousFileSha256 { get; set; }

    /// <summary>How many times the file's hash changed while its version number did not. More than zero means the author replaced it in place.</summary>
    public int FileHashChanges { get; set; }

    /// <summary>The plugin class's name, and what its <c>[Info]</c> attribute says, for a <c>cs</c> file that has one. Text from the file: untrusted.</summary>
    [MaxLength(200)]
    public string? PluginClassName { get; set; }

    [MaxLength(200)]
    public string? PluginInfoName { get; set; }

    [MaxLength(200)]
    public string? PluginInfoAuthor { get; set; }

    [MaxLength(50)]
    public string? PluginInfoVersion { get; set; }

    /// <summary>For a zip: the files inside, one per line as <c>path</c>, a tab and the declared size, up to <see cref="MaxZipEntries"/> - what a person's instructions and a saved mapping are checked against (<c>ZipListing</c>).</summary>
    public string? ZipEntries { get; set; }

    /// <summary>
    /// For a zip: what looking inside each plugin source (<c>.cs</c>) file in it found - class, <c>[Info]</c> name and version, and whether it reads as C# -
    /// as JSON (<c>ZipSourceFinding</c>). Recorded once, with the hash, so a person's instructions can be checked against it without the archive being
    /// fetched again. <c>null</c> for anything that is not a zip, and for a zip checked before this was kept (it is checked again).
    /// </summary>
    public string? ZipSourceFindings { get; set; }

    public const int MaxZipEntries = 500;

    public DateTimeOffset? ValidatedAtUtc { get; set; }

    /// <summary>When a <see cref="PluginFileValidationState.Failed"/> file is tried again.</summary>
    public DateTimeOffset? ValidationNextAttemptUtc { get; set; }

    public int ValidationAttempts { get; set; }
}
