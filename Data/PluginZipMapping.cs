// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// The instructions a person gave for a plugin that ships as a zip archive on one server: which folders go where, and what is skipped. Saved so the next
/// update, if it contains only the same files in the same structure, can be applied without asking again. Per server and per plugin: where files go is
/// a fact about how one server is laid out, and does not carry to another.
/// </summary>
/// <remarks>
/// A saved mapping is used automatically only once it is <see cref="Trusted"/>, which happens when an update applied with it came up loaded. A mapping
/// that has only been given, never confirmed by a success, is offered back to the person to apply once by hand. Whether a new archive is still covered is
/// worked out from the rules and the archive's files each time (<c>ZipMapping.Resolve</c>), never stored: a new file inside a mapped folder is covered,
/// a new top-level folder or file is not, and the update then waits for the person.
/// </remarks>
[Table("PluginZipMapping")]
[Index(nameof(RustServerId), nameof(NormalizedName), IsUnique = true, Name = "IX_PluginZipMapping_Server_Plugin")]
public class PluginZipMapping : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The plugin, in the form update notices are matched on.</summary>
    [Required]
    [MaxLength(200)]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>The rules, as JSON (<c>ZipMappingRule</c>).</summary>
    [Required]
    public string RulesJson { get; set; } = "[]";

    /// <summary>Whether an update applied with these rules has come up loaded, so they can be used without asking.</summary>
    public bool Trusted { get; set; }

    public DateTimeOffset SavedAtUtc { get; set; }

    public DateTimeOffset? LastAppliedAtUtc { get; set; }
}
