// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.Auditing;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A tenant moderator's internal note on a <see cref="ServerReport"/> - what they checked, what they decided and why.
/// </summary>
/// <remarks>
/// Distinct from <see cref="Note"/> (a site admin's CRM annotation on an Organization or person) and <see cref="TicketNote"/> (a
/// site admin's note on a support ticket): this one is written by the organization's own members about the reports its own players
/// filed. The author is <see cref="ICreatable.CreatedById"/>, filled in when the note is added. Append-only - there is no edit.
/// Removed with its report (cascade), which is itself removed with its server.
/// </remarks>
[Table("ServerReportNote")]
[Index(nameof(ServerReportId), nameof(CreatedOn), Name = "IX_ServerReportNote_ServerReportId_CreatedOn")]
public class ServerReportNote : AuditableEntity, ITenantScoped
{
    public const int MaxContentLength = 4000;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid ServerReportId { get; set; }
    public ServerReport ServerReport { get; set; } = null!;

    [Required]
    [MaxLength(MaxContentLength)]
    public string Content { get; set; } = string.Empty;
}
