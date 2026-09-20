// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// The current picture of one server's bases: every tool cupboard the RustArchon plugin has indexed, with its position,
/// owner and who is authorized. One row per server, replaced wholesale each time the Worker reads the plugin - a snapshot,
/// not a history (a history of bases is a later feature).
/// </summary>
/// <remarks>
/// This is sensitive: it says where players live and who they share a base with. Reading it needs its own permission,
/// <see cref="Infrastructure.PermissionCatalog.ServerViewBases"/>, held by an Organization's Owner and delegable to roles.
/// </remarks>
[Table("PluginTcSnapshot")]
[Index(nameof(RustServerId), IsUnique = true, Name = "IX_PluginTcSnapshot_RustServerId")]
public class PluginTcSnapshot : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The plugin's initial scan of the world had finished, so the list is complete. False means it may be missing cupboards.</summary>
    public bool Ready { get; set; }

    public int Count { get; set; }

    /// <summary>The plugin's format the data is in. Only 1 exists.</summary>
    public int Format { get; set; } = 1;

    /// <summary>The cupboards: a JSON array in the plugin's compact format, gzipped.</summary>
    [Required]
    public byte[] Data { get; set; } = [];

    /// <summary>When the Worker read this from the plugin.</summary>
    public DateTimeOffset CapturedAtUtc { get; set; }
}
