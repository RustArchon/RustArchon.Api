// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// The newest update UpdateChecker has reported for one plugin on one server: everything it said, as it said it. One row per server per
/// plugin name (case-insensitive), replaced when the newest version changes. It is a fact about what a third-party plugin claimed, kept
/// as reported; whether it still holds is decided when it is read, against the plugin list the Worker last polled.
/// </summary>
[Table("PluginUpdateNotice")]
[Index(nameof(RustServerId), nameof(NormalizedName), IsUnique = true, Name = "IX_PluginUpdateNotice_Server_Name")]
public class PluginUpdateNotice : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The name in lower case, for matching. The plugin's own capitalization is kept in <see cref="Name"/>.</summary>
    [Required]
    [MaxLength(100)]
    public string NormalizedName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string CurrentVersion { get; set; } = string.Empty;

    [MaxLength(50)]
    public string LatestVersion { get; set; } = string.Empty;

    /// <summary>As reported, unchecked. Never link to it without checking the scheme.</summary>
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Marketplace { get; set; } = string.Empty;

    /// <summary>When the RustArchon plugin first heard of this newest version (kept across plugin reloads).</summary>
    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>When it was last heard.</summary>
    public DateTimeOffset LastSeenUtc { get; set; }

    public int TimesSeen { get; set; }

    /// <summary>When the Api last received a report that included this notice.</summary>
    public DateTimeOffset ReportedAtUtc { get; set; }
}
