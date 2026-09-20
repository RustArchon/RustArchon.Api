// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>Where a plugin signing key stands. There is exactly one <see cref="Active"/> key at any time.</summary>
public enum PluginKeyState
{
    /// <summary>The key stamped into newly served files. Held in the <c>PluginSigningKey</c> platform setting.</summary>
    Active = 0,

    /// <summary>
    /// Superseded, but still kept: a server that still trusts it is updated by a "bridge" signed with it that
    /// embeds the active key. Never removed, because absence of reports from servers proves nothing.
    /// </summary>
    Retired = 1,

    /// <summary>
    /// Compromised or otherwise withdrawn. Never signs again; a server still on it can only be fixed by downloading
    /// the plugin by hand. Kept (not deleted) so the history stays complete.
    /// </summary>
    Revoked = 2
}

/// <summary>
/// A plugin signing key that is no longer the active one. The active key lives in the <c>PluginSigningKey</c>
/// platform setting; rotating moves it here, so <see cref="EncryptedPrivateKey"/> holds exactly the string the setting
/// held (encrypted with the same purpose, so it moves over unchanged) and is decrypted only to sign a bridge.
/// </summary>
/// <remarks>Platform-wide, not tenant scoped: one signing identity per deployment. A revoked row's private half is kept
/// only so the history is complete; nothing signs with it.</remarks>
[Table("PluginKeyHistory")]
[Index(nameof(Fingerprint), IsUnique = true, Name = "IX_PluginKeyHistory_Fingerprint")]
public class PluginKeyHistory : Entity
{
    /// <summary>First 16 hex characters of SHA-256 over the public modulus.</summary>
    [Required]
    [MaxLength(16)]
    public string Fingerprint { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string ModulusBase64 { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string ExponentBase64 { get; set; } = string.Empty;

    /// <summary>The private key, encrypted by <c>IApiKeyProtector</c> (purpose <c>PluginSigningKey</c>).</summary>
    [Required]
    [MaxLength(8000)]
    public string EncryptedPrivateKey { get; set; } = string.Empty;

    /// <summary><see cref="PluginKeyState.Retired"/> or <see cref="PluginKeyState.Revoked"/>; never Active.</summary>
    public PluginKeyState State { get; set; } = PluginKeyState.Retired;

    public DateTimeOffset RetiredAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }

    [MaxLength(500)]
    public string? RevokedReason { get; set; }
}
