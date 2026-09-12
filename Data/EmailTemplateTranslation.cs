// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data.Auditing;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One language's Subject/HtmlBody for a given <see cref="EmailTemplate"/> - the child table design B
/// settled on so <see cref="EmailTemplate.Placeholders"/> stays declared once per template rather than
/// duplicated per language. See <see cref="Infrastructure.EmailTemplateRegistry"/> for where the
/// <c>"en-US"</c> row of each template is seeded, and <see cref="Administration.CommunicationPublisher"/>
/// for how a send resolves which row to actually use.
/// </summary>
/// <remarks>
/// There is deliberately no Api-side notion of "the supported cultures" this table's rows are checked
/// against - that list lives in RustArchon.Panel, as whichever <c>Resources/SharedResource.*.json</c>
/// files are compiled in. The admin editor (Panel) is the one that knows which cultures to expect and
/// therefore which of them have no row here yet.
/// </remarks>
[Table("EmailTemplateTranslation")]
[Index(nameof(EmailTemplateId), nameof(Culture), IsUnique = true, Name = "IX_EmailTemplateTranslation_EmailTemplateId_Culture")]
public class EmailTemplateTranslation : AuditableEntity
{
    /// <summary>Gets or sets the owning <see cref="EmailTemplate"/>'s id.</summary>
    [Required]
    public Guid EmailTemplateId { get; set; }

    /// <summary>Gets or sets the owning template - see <see cref="EmailTemplateId"/>.</summary>
    public EmailTemplate? EmailTemplate { get; set; }

    /// <summary>
    /// Gets or sets the culture this translation is written in (e.g. <c>"en-US"</c>) - the same
    /// <see cref="System.Globalization.CultureInfo.Name"/> string RustArchon.Panel's
    /// <c>IStringLocalizer</c> and <c>RequestLocalizationOptions.SupportedUICultures</c> use.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Culture { get; set; } = string.Empty;

    /// <summary>Gets or sets the email subject line, with <c>{{Token}}</c> placeholders.</summary>
    [Required]
    [MaxLength(500)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the HTML body, with <c>{{Token}}</c> placeholders - before the tracking pixel
    /// <see cref="Administration.CommunicationPublisher"/> appends at send time.
    /// </summary>
    [Required]
    public string HtmlBody { get; set; } = string.Empty;
}
