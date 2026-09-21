// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One reason a plugin file on a server did not load, as Carbon lists it under "failed plugins" - see
/// <see cref="Messaging.Contracts.ServerPluginFailure"/>.
/// </summary>
/// <remarks>
/// Like <see cref="ServerPlugin"/>, the server's <em>current</em> set, not history: each poll that reports failures makes a server's rows match
/// the report exactly, so a plugin that has been fixed disappears. Derives from <see cref="Entity"/>, not an auditable variant, since nothing a
/// person does creates one.
/// </remarks>
[Table("PluginLoadFailure")]
[Index(nameof(TenantId), nameof(RustServerId), nameof(FileName), Name = "IX_PluginLoadFailure_TenantId_RustServerId_FileName")]
public class PluginLoadFailure : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The plugin's file name as Carbon shows it, for example <c>BotReSpawn.cs</c>.</summary>
    [Required]
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    public int Line { get; set; }
    public int Column { get; set; }

    [Required]
    [MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    /// <summary>When the poll that last confirmed this reason ran.</summary>
    public DateTimeOffset CapturedAtUtc { get; set; }
}
