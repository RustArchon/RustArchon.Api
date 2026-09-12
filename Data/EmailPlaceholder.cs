// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data.Auditing;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One reusable <c>{{Token}}</c> placeholder an <see cref="EmailTemplate"/> can use - its exact
/// substitution name, an admin-facing explanation, and a realistic <see cref="Sample"/> value the
/// admin preview substitutes in its place. See <see cref="Infrastructure.EmailTemplateRegistry"/> for
/// where placeholders are declared, seeded, and linked to the templates that use them.
/// </summary>
/// <remarks>
/// <para>
/// Named "Placeholder" rather than "Token" specifically to avoid this app's other, unrelated senses of
/// that word - a JWT, a <c>TenantInvitation.Token</c>, an RCON/Steam/Geolocation API key. All of those
/// live in this same codebase; a bare <c>Token</c> entity here would be a standing source of "which
/// token do you mean" confusion.
/// </para>
/// <para>
/// Deliberately its own entity rather than a comma-separated string on <see cref="EmailTemplate"/>
/// (an earlier version of this worked that way) - the sample value only has a legitimate place to live
/// once a placeholder is a real row, and multiple templates sharing a placeholder (the common case:
/// <c>OrganizationName</c> will show up on every organization-level email RustArchon ever sends) should
/// share one Sample rather than each re-typing it.
/// </para>
/// <para>
/// Many-to-many with <see cref="EmailTemplate"/> via a plain EF Core skip-navigation join table
/// (<c>EmailTemplatePlaceholder</c>, configured in <c>ApiDbContext.OnModelCreating</c>) - nothing about
/// the link itself carries data worth its own modeled entity, unlike e.g. <c>RolePermission</c>.
/// </para>
/// </remarks>
[Table("EmailPlaceholder")]
[Index(nameof(Name), IsUnique = true, Name = "IX_EmailPlaceholder_Name")]
public class EmailPlaceholder : AuditableEntity
{
    /// <summary>
    /// Gets or sets the exact substitution name - what appears between the braces (e.g.
    /// <c>"OrganizationName"</c> for the <c>{{OrganizationName}}</c> placeholder), and the same string
    /// callers use as a token dictionary key when queuing a templated email.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a short explanation of what this placeholder stands for, shown to the
    /// admin next to its "insert" chip.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets a realistic example value substituted in wherever this placeholder appears in the
    /// admin preview - e.g. <c>"Acme Corporation"</c> for <c>OrganizationName</c>. Never sent to a real
    /// recipient; see <c>RustArchon.Panel.Services.EmailTemplatePreviewRenderer</c>, the only place
    /// this is read.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public string Sample { get; set; } = string.Empty;

    /// <summary>Gets the templates that use this placeholder.</summary>
    public ICollection<EmailTemplate> Templates { get; set; } = [];
}
