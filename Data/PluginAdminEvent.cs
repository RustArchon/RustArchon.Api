// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>What a platform admin did to the plugin signing keys or releases.</summary>
public enum PluginAdminEventKind
{
    KeyRotated = 1,
    KeyRevoked = 2,
    ReleaseUploaded = 3,
    ReleasePublished = 4,
    ReleaseWithdrawn = 5,
    KeysExported = 6,
    KeysImported = 7,

    /// <summary>The deployment's first signing key was made (the first time the plugin was downloaded). Actor is "system".</summary>
    KeyGenerated = 8,

    /// <summary>An administrator had a one-off file signed with the active key and downloaded it, without publishing it.</summary>
    FileSigned = 9,

    /// <summary>An administrator downloaded a stored release signed with the active key, to test it before (or without) publishing it.</summary>
    ReleaseSigned = 10
}

/// <summary>
/// One line of the audit log for the sensitive platform-admin actions around the RustArchon plugin: rotating or
/// revoking a signing key, and uploading, publishing or withdrawing a release. Append-only - nothing edits or deletes
/// these - because what they record (who changed the code every connected game server will run, and the key that
/// vouches for it) is exactly what has to be answerable later.
/// </summary>
[Table("PluginAdminEvent")]
[Index(nameof(AtUtc), Name = "IX_PluginAdminEvent_AtUtc")]
public class PluginAdminEvent : Entity
{
    public DateTimeOffset AtUtc { get; set; }

    public PluginAdminEventKind Kind { get; set; }

    /// <summary>What it was done to: a key fingerprint, or "Main 0.2.3" / "Updater 0.2.0" for a release.</summary>
    [Required]
    [MaxLength(100)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>Who did it (their email, else their name).</summary>
    [Required]
    [MaxLength(200)]
    public string Actor { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Detail { get; set; }
}
