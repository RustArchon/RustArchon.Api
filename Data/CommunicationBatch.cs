// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.Auditing;

namespace RustArchon.Api.Data;

/// <summary>
/// One message a site admin sent to a group of organizations at once - the record that ties the individual <see cref="Communication"/>s of that send
/// together, so "sent to 38 organizations on 21 September" can be seen and a second send of the same thing is visible for what it is.
/// </summary>
/// <remarks>
/// Each recipient still gets their own <see cref="Communication"/> (with the exact body they received); this holds what was chosen - who it was for and how the
/// words were sent - not the words themselves, which differ by recipient (their name, their language).
/// </remarks>
[Table("CommunicationBatch")]
public class CommunicationBatch : AuditableEntity
{
    /// <summary>What kind of send this was; today only <see cref="Kinds.PlanAnnouncement"/>.</summary>
    [Required]
    [MaxLength(50)]
    public string Kind { get; set; } = Kinds.PlanAnnouncement;

    public static class Kinds
    {
        public const string PlanAnnouncement = "PlanAnnouncement";
    }

    /// <summary>The plan the send was started from.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Whether the plans that superseded it were included, or only that plan.</summary>
    public bool IncludeSupersedingVersions { get; set; }

    /// <summary>Whether organizations with an overdue invoice were included.</summary>
    public bool IncludePastDue { get; set; }

    /// <summary><c>PerLanguage</c> (each recipient got their language's version) or <c>SingleLanguage</c> (everyone got one).</summary>
    [MaxLength(20)]
    public string LanguageMode { get; set; } = string.Empty;

    /// <summary>The language everyone got, for a single-language send; otherwise null.</summary>
    [MaxLength(35)]
    public string? SingleCulture { get; set; }

    /// <summary>The subject as written in the default language (or the one language sent) - for recognising the send in a list.</summary>
    [MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>How many emails were queued.</summary>
    public int RecipientCount { get; set; }

    /// <summary>How many organizations in scope were left out, because they could not be reached.</summary>
    public int SkippedCount { get; set; }

    /// <summary>The site admin who sent it.</summary>
    public Guid? SentById { get; set; }

    public DateTimeOffset SentOn { get; set; }
}
