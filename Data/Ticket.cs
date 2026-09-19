// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using JumpStart.Data;
using JumpStart.Data.Auditing;
using JumpStart.Data.MultiTenant;

namespace RustArchon.Api.Data;

/// <summary>
/// A support request, from submission through resolution - the record a site admin works from
/// <c>Admin/Tickets</c>, and the submitter tracks either as a signed-in tenant user or, for an
/// anonymous marketing-site submission, via <see cref="GuestAccessToken"/>.
/// </summary>
/// <remarks>
/// <para>
/// The submitter is always the tenant/organization (or a prospect with no tenant yet), never one of
/// a tenant's own players - there is no screen where a player sees this.
/// </para>
/// <para>
/// <see cref="TenantId"/> and <see cref="SubmitterUserId"/> are both nullable because a submission
/// from the marketing site's contact form has neither at first - see <see cref="GuestAccessToken"/>.
/// Signing up afterward links both, the same way an Organization invitation addresses an email
/// nobody has registered yet (see <see cref="Communication"/>'s remarks on <c>UserId</c>).
/// </para>
/// <para>
/// This is distinct from <see cref="Communication"/>, which is a one-way outbound-email audit log -
/// a <see cref="Ticket"/> is the two-way conversation itself, carried by its <see cref="TicketMessage"/>
/// rows; a <see cref="Communication"/> row is created only when this system happens to notify someone
/// by email about a change here, same as any other transactional email.
/// </para>
/// </remarks>
public class Ticket : AuditableEntity, ITenantScopedOptional
{
    /// <summary>The Organization this ticket belongs to, once known - see this class's remarks.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Navigation to <see cref="TenantId"/>.</summary>
    public Tenant? Tenant { get; set; }

    /// <summary>The signed-in tenant user who submitted this, once known - see this class's remarks.</summary>
    public Guid? SubmitterUserId { get; set; }

    /// <summary>
    /// The submitter's own email address - always populated, unlike <see cref="SubmitterUserId"/>,
    /// since it's the one thing every submission genuinely has (same reasoning as
    /// <see cref="Communication.ToAddress"/>).
    /// </summary>
    [Required]
    [StringLength(320)]
    public string SubmitterEmail { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string SubmitterName { get; set; } = string.Empty;

    public Guid QueueId { get; set; }

    public Queue Queue { get; set; } = null!;

    [Required]
    [StringLength(500)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>The <see cref="Data.TicketStatus"/> this ticket is currently in - see
    /// <c>TicketStatusSeeder.Slugs.Submitted</c> for the one a fresh ticket starts in.</summary>
    public Guid StatusId { get; set; }

    /// <summary>Navigation to <see cref="StatusId"/>.</summary>
    public TicketStatus Status { get; set; } = null!;

    /// <summary>The staff member currently working this, if any.</summary>
    public Guid? AssignedToUserId { get; set; }

    /// <summary>
    /// When this ticket was created - its own column, same reasoning as <see cref="Communication.QueuedOn"/>,
    /// so the staff console's "Submitted" column reads directly off a name that says what it means.
    /// </summary>
    public DateTimeOffset SubmittedOn { get; set; }

    public DateTimeOffset? ResolvedOn { get; set; }

    public DateTimeOffset? ClosedOn { get; set; }

    /// <summary>
    /// A per-ticket abuse guard a staff member sets from the staff console: while this ticket is
    /// closed, a customer/guest reply is refused outright instead of moving it to <c>Reopened</c> - see
    /// <c>TicketReplyPolicy</c>. Deliberately on the ticket, not on <see cref="Data.TicketStatus"/> -
    /// this is about one specific ticket a staff member has decided should stay closed, not a blanket
    /// rule for every ticket ever in that status.
    /// </summary>
    public bool PreventReopening { get; set; }

    /// <summary>
    /// A long-lived, unguessable token letting an anonymous submitter view and reply to this one
    /// ticket without an account - set only when there is no <see cref="SubmitterUserId"/> at
    /// creation time. Unlike an invitation token, this is meant for repeat use (the submitter comes
    /// back for every reply), not single-redemption, so it lives directly on the ticket rather than
    /// in a separate redemption-tracking table.
    /// </summary>
    [StringLength(64)]
    public string? GuestAccessToken { get; set; }

    /// <summary>
    /// When <see cref="GuestAccessToken"/> stops working. Re-issued (pushed forward) every time a
    /// notification email is sent for this ticket, so an anonymous submitter who keeps engaging is
    /// never locked out mid-conversation.
    /// </summary>
    public DateTimeOffset? GuestAccessTokenExpiresOn { get; set; }
}
