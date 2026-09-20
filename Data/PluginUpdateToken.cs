// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A single-use, short-lived permission for one game server to download the current plugin script from this Panel -
/// what an admin-triggered plugin update hands the Updater instead of a standing credential.
/// </summary>
/// <remarks>
/// <para>
/// Only a <b>hash</b> of the token is stored (SHA-256, hex): the token itself exists in the URL sent to the game
/// server and nowhere at rest, so a database dump cannot be used to fetch anything. It is bound to one server (a token
/// for server A is refused for server B), expires (ten minutes by default), and is redeemed by one atomic update, so
/// two requests presenting it can never both succeed. A refusal never says which of those it was.
/// </para>
/// <para>
/// Derives from <see cref="Entity"/>: nobody acts on it, the system mints and redeems it. Expired rows are purged
/// opportunistically whenever a new one is minted, so the table cannot grow without bound.
/// </para>
/// </remarks>
/// <summary>The files a <see cref="PluginUpdateToken"/> can be redeemed for.</summary>
public static class PluginUpdateTokenPurposes
{
    public const string Main = "main";
    public const string Updater = "updater";
}

[Table("PluginUpdateToken")]
[Index(nameof(TokenHash), IsUnique = true, Name = "IX_PluginUpdateToken_TokenHash")]
[Index(nameof(ExpiresAtUtc), Name = "IX_PluginUpdateToken_ExpiresAtUtc")]
public class PluginUpdateToken : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>
    /// The key fingerprint the server reported when the token was minted: the key its installed plugin trusts, so the
    /// file it downloads is signed with that key (a "bridge" to the active key if they differ). Empty only on a token
    /// minted before this existed, which then cannot be redeemed for a download.
    /// </summary>
    [MaxLength(16)]
    public string SigningKeyFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// What the token may download: <see cref="PluginUpdateTokenPurposes.Main"/> (the RustArchon plugin, what every token was before this
    /// existed) or <see cref="PluginUpdateTokenPurposes.Updater"/> (the Updater plugin). A token is good for exactly one of them.
    /// </summary>
    [Required]
    [MaxLength(16)]
    public string Purpose { get; set; } = PluginUpdateTokenPurposes.Main;

    /// <summary>Lower-case hex SHA-256 of the token. Never the token.</summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>When it was used; <c>null</c> while still available. A used token can never be used again.</summary>
    public DateTimeOffset? RedeemedAtUtc { get; set; }
}
