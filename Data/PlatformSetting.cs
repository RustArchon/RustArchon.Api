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
    PlanReference,

    /// <summary>
    /// <see cref="PlatformSetting.Value"/> is encrypted at rest via <c>IApiKeyProtector</c> (same
    /// mechanism as <see cref="RustServer.SteamApiKey"/>/<see cref="RustServer.GeolocationApiKey"/>,
    /// with its own purpose string per setting - see <c>ApiKeyProtectorPurposes</c>) rather than stored
    /// as plain text. <c>PlatformSettingsController</c> never returns the actual value for a setting of
    /// this type to a browser - only whether one is currently set (see
    /// <c>Shared.DTOs.PlatformSettingDto.HasValue</c>) - so there is no way for an admin's form
    /// submission to mean "leave it unchanged" the way an ordinary field's round-tripped value would;
    /// the Panel's own UI simply never submits an update for a secret field left blank, and the
    /// endpoint refuses an empty value outright rather than guessing whether blank meant "unchanged" or
    /// "clear it." Introduced for <see cref="Infrastructure.PlatformSettingsRegistry.EmailSmtpPassword"/>
    /// and <see cref="Infrastructure.PlatformSettingsRegistry.EmailApiKey"/>.
    /// </summary>
    Secret = 4,

    /// <summary>
    /// <see cref="PlatformSetting.Value"/> is one of the fixed, code-defined options listed in
    /// <see cref="PlatformSetting.Options"/> (comma-separated) - the admin UI renders this as a
    /// <c>&lt;select&gt;</c>. One generic renderer serves every setting of this type, the same way
    /// <see cref="PlanReference"/>'s serves every Plan-valued one - the options are data on the row,
    /// not a UI branch tied to one specific setting's name.
    /// </summary>
    Choice = 5
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
    /// Gets or sets which section of the admin settings page this belongs under (e.g. <c>"Email"</c>).
    /// A plain string, not a foreign key to some category table - the set of categories is exactly as
    /// fixed and code-defined as the set of settings themselves (see
    /// <see cref="Infrastructure.PlatformSettingsRegistry"/>), so a full relational model would only
    /// add a table for the same "add a row in code" workflow this whole entity already exists to avoid.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets this setting's sort position within <see cref="Category"/>, ascending. Explicit
    /// rather than sorting by <see cref="DisplayName"/> - alphabetical order interleaves settings that
    /// belong together conceptually (e.g. an email provider picker landing between "API key" and
    /// "From address" purely because of spelling) with no way to fix it short of renaming things to
    /// spell in the order you want, which is not what a display name is for.
    /// </summary>
    public int Order { get; set; }

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
    /// Gets or sets the allowed values for a <see cref="PlatformSettingValueType.Choice"/> setting,
    /// comma-separated (e.g. <c>"Smtp,Resend,SendGrid"</c>) - null for every other <see cref="ValueType"/>.
    /// </summary>
    [MaxLength(500)]
    public string? Options { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="Key"/> of another setting that controls whether this one is shown
    /// at all in the admin UI - null if this setting is always shown.
    /// </summary>
    /// <remarks>
    /// Exists so a setting that's meaningless under the current choice of another one doesn't have to
    /// be shown, disabled and unexplained - the SMTP fields have nothing to do with the page while an
    /// API-based provider is selected, and showing them anyway is exactly the clutter this whole
    /// mechanism exists to avoid. Evaluated entirely in the Panel (<c>PlatformSettings.razor</c>); the
    /// Api enforces nothing from it - a value saved while its controlling setting says otherwise is
    /// still valid data, just not currently the thing in effect (see
    /// <c>RustArchon.Worker</c>'s own reasoning for which email settings actually apply).
    /// </remarks>
    [MaxLength(100)]
    public string? VisibleWhenKey { get; set; }

    /// <summary>
    /// Gets or sets the value <see cref="VisibleWhenKey"/>'s setting must currently hold (or, if
    /// <see cref="VisibleWhenNegate"/>, must <em>not</em> hold) for this setting to be shown.
    /// </summary>
    [MaxLength(200)]
    public string? VisibleWhenValue { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="VisibleWhenValue"/>'s comparison is inverted - "show me when
    /// it's anything else." Lets one rule ("not Smtp") cover every current and future API-based
    /// provider without listing them, rather than a whitelist that needs editing each time a new one
    /// is added.
    /// </summary>
    public bool VisibleWhenNegate { get; set; }

    /// <summary>
    /// Gets or sets the setting's current value, always stored as its literal string form
    /// (<c>"true"</c>/<c>"false"</c> for <see cref="PlatformSettingValueType.Boolean"/>, the decimal
    /// digits for <see cref="PlatformSettingValueType.Integer"/>) regardless of <see cref="ValueType"/>.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public string Value { get; set; } = string.Empty;
}
