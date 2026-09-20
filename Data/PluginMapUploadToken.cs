// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A single-use, short-lived permission for one game server to send one world's map picture to this Panel. The same
/// design as <see cref="PluginUpdateToken"/> (only a hash is stored; bound to one server; expires; redeemed by one atomic
/// update), and additionally bound to the <see cref="PluginMap"/> it is for, so a token cannot attach a picture to a
/// different world.
/// </summary>
[Table("PluginMapUploadToken")]
[Index(nameof(TokenHash), IsUnique = true, Name = "IX_PluginMapUploadToken_TokenHash")]
[Index(nameof(ExpiresAtUtc), Name = "IX_PluginMapUploadToken_ExpiresAtUtc")]
public class PluginMapUploadToken : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The map row this token entitles its holder to complete.</summary>
    public Guid PluginMapId { get; set; }

    /// <summary>Lower-case hex SHA-256 of the token. Never the token.</summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>When it was used; null while still available. A used token can never be used again.</summary>
    public DateTimeOffset? RedeemedAtUtc { get; set; }
}
