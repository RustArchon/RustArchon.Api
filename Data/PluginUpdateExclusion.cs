// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A plugin a person has said never to update automatically or by hand on one server - not a preference about pace or timing, but that the update
/// notice itself does not apply here. The case this exists for: an UpdateChecker notice naming the free uMod listing of a plugin a person actually
/// runs a different, paid build of (bought directly from its author), where the "latest version" on record is not a newer version of what is
/// installed at all. Per server and per plugin: what is actually installed is a fact about one server, not the organization.
/// </summary>
[Table("PluginUpdateExclusion")]
[Index(nameof(RustServerId), nameof(NormalizedName), IsUnique = true, Name = "IX_PluginUpdateExclusion_Server_Plugin")]
public class PluginUpdateExclusion : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The plugin, in the form update notices are matched on.</summary>
    [Required]
    [MaxLength(200)]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Why, in the person's own words - shown back to them wherever the exclusion is. Never required: the exclusion itself is the decision.</summary>
    [MaxLength(500)]
    public string Note { get; set; } = string.Empty;

    public DateTimeOffset ExcludedAtUtc { get; set; }
}
