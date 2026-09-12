// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data.Auditing;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One editable email template - the code-level identity and placeholder set shared by every language
/// it's sent in. The actual Subject/HtmlBody wording lives one level down, in
/// <see cref="Translations"/> (one <see cref="EmailTemplateTranslation"/> per culture) - see that
/// class's own remarks for why. See <see cref="Infrastructure.EmailTemplateRegistry"/> for where the
/// known template Codes are declared and seeded, and <see cref="Administration.EmailTemplateRenderer"/>
/// for how placeholders are filled in.
/// </summary>
/// <remarks>
/// Platform-wide, not tenant-scoped - the same shape as <see cref="PlatformSetting"/> and for the same
/// reason: wording is a global decision.
/// </remarks>
[Table("EmailTemplate")]
[Index(nameof(Code), IsUnique = true, Name = "IX_EmailTemplate_Code")]
public class EmailTemplate : AuditableEntity
{
    /// <summary>
    /// Gets or sets the unique, stable identifier code calling code asks for (e.g.
    /// <c>"OrganizationInvitation"</c>) - never shown to an admin; <see cref="Name"/> is what the admin
    /// UI renders instead.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets a short, human-readable label for the admin UI.</summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a longer explanation of when this template is used, shown under <see cref="Name"/>
    /// in the admin UI.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets the <see cref="EmailPlaceholder"/>s this template's caller actually supplies - shown in
    /// the admin UI as "insert" chips next to the editor. Nothing enforces that a saved
    /// <see cref="EmailTemplateTranslation.Subject"/>/<see cref="EmailTemplateTranslation.HtmlBody"/>
    /// actually uses only these, or all of these; see <see cref="Administration.EmailTemplateRenderer"/>'s
    /// remarks for what happens to a placeholder an admin references that isn't supplied at send time.
    /// Shared across every language this template is translated into - a placeholder set is a property
    /// of what the template is for, not of which language it happens to be written in.
    /// </summary>
    public ICollection<EmailPlaceholder> Placeholders { get; set; } = [];

    /// <summary>Gets the per-culture Subject/HtmlBody rows for this template - see
    /// <see cref="EmailTemplateTranslation"/>.</summary>
    public ICollection<EmailTemplateTranslation> Translations { get; set; } = [];
}
