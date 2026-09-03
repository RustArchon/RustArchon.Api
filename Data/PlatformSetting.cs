// Copyright ©2026 Scott Blomfield

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data.Auditing;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// Identifies how a <see cref="PlatformSetting.Value"/> string should be parsed and how the admin UI
/// should present it for editing.
/// </summary>
public enum PlatformSettingValueType
{
    Boolean,
    String,
    Integer,

    /// <summary>
    /// <see cref="PlatformSetting.Value"/> is either empty (unset) or a <see cref="Plan.Id"/> Guid,
    /// stored as its string form same as every other value type. Introduced for
    /// <see cref="Infrastructure.PlatformSettingsRegistry.DefaultPlanId"/> - lets the admin UI render a
    /// plan picker instead of a free-text box, and lets a future setting reuse the same picker for
    /// another Plan-valued choice without inventing a second mechanism.
    /// </summary>
    PlanReference
}

/// <summary>
/// A single platform-wide (not tenant-scoped) named setting, generically shaped as a key/value pair
/// rather than a strongly-typed column-per-setting row.
/// </summary>
/// <remarks>
/// <para>
/// Chosen over a singleton "one column per setting" table specifically because RustArchon expects a
/// growing set of these - a generic key/value table means adding a new setting is a row (seeded by
/// <see cref="Infrastructure.PlatformSettingsRegistry"/>), never a migration. The trade-off is that
/// <see cref="Value"/> is always a plain string; <see cref="ValueType"/> exists precisely to recover
/// enough type information for the admin UI and <see cref="Infrastructure.IPlatformSettingsCache"/>'s
/// typed getters to parse it back correctly.
/// </para>
/// <para>
/// Not <see cref="JumpStart.Data.MultiTenant.ITenantScoped"/> - these settings apply platform-wide,
/// to every tenant, by design (see <see cref="Controllers.PlatformSettingsController"/>'s remarks for
/// why this is gated by a global permission rather than any tenant's own role).
/// </para>
/// </remarks>
[Table("PlatformSetting")]
[Index(nameof(Key), IsUnique = true, Name = "IX_PlatformSetting_Key")]
public class PlatformSetting : AuditableEntity
{
    /// <summary>
    /// Gets or sets the setting's unique, stable name (e.g. <c>"InvitationCodesEnabled"</c>) - the
    /// same string used as its Valkey cache key and in application code to read it. Never shown to
    /// end users; <see cref="DisplayName"/> is what the admin UI renders instead.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a short, human-readable label for the admin UI (e.g. "Require invitation codes
    /// to register").
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a longer explanation of what this setting controls, shown under
    /// <see cref="DisplayName"/> in the admin UI.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets how <see cref="Value"/> should be parsed/rendered - see
    /// <see cref="PlatformSettingValueType"/>.
    /// </summary>
    public PlatformSettingValueType ValueType { get; set; } = PlatformSettingValueType.String;

    /// <summary>
    /// Gets or sets the setting's current value, always stored as its literal string form
    /// (<c>"true"</c>/<c>"false"</c> for <see cref="PlatformSettingValueType.Boolean"/>, the decimal
    /// digits for <see cref="PlatformSettingValueType.Integer"/>) regardless of <see cref="ValueType"/>.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public string Value { get; set; } = string.Empty;
}
