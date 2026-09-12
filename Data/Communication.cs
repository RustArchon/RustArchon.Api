// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using JumpStart.Data;
using JumpStart.Data.Auditing;
using JumpStart.Data.MultiTenant;

namespace RustArchon.Api.Data;

/// <summary>
/// Where a <see cref="Communication"/> currently stands. A one-way progression, except that a
/// <see cref="Queued"/> communication can also end at <see cref="Cancelled"/> instead of
/// <see cref="Sent"/>.
/// </summary>
/// <remarks>
/// <see cref="Bounced"/> here means the provider could not deliver it at all (SMTP connection
/// refused, credentials rejected, recipient rejected outright) - reported synchronously by
/// <c>RustArchon.Worker</c>'s own send attempt. It does not cover an asynchronous bounce (a deferred
/// or soft bounce reported later by the receiving mail server, a webhook from a provider like
/// Resend) - there is no mailbox-monitoring or webhook pipeline behind this yet, so a message the
/// provider accepted and later silently dropped still shows as <see cref="Sent"/>. Worth building if
/// that gap turns out to matter in practice; not built speculatively here.
/// </remarks>
public enum CommunicationStatus
{
    /// <summary>Durably published, not yet attempted.</summary>
    Queued,

    /// <summary>The provider accepted it.</summary>
    Sent,

    /// <summary>The provider could not deliver it - see this enum's remarks.</summary>
    Bounced,

    /// <summary>Sent, and the recipient has opened it at least once - see <see cref="Communication.ViewedOn"/>.</summary>
    Viewed,

    /// <summary>Withdrawn while still <see cref="Queued"/> - see <c>CommunicationsController.Cancel</c>.</summary>
    Cancelled
}

/// <summary>
/// A permanent record of one outbound email - what was sent, who it went to, and its delivery
/// lifecycle. Every email this system sends is meant to leave one of these behind; nothing here is
/// ever deleted, only appended to as the message moves through <see cref="CommunicationStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UserId"/> is the member it went to, when the recipient corresponds to an existing
/// account - nullable rather than required, because it doesn't always: an Organization invitation is
/// addressed to an email address nobody has registered yet (see
/// <c>OrganizationInvitationService.SendInvitationEmailAsync</c>). <see cref="ToAddress"/> is the one
/// field that's always populated, since it's the one thing every send genuinely has.
/// </para>
/// <para>
/// <see cref="TenantId"/> is set only for an organization-level communication - a renewal notice, an
/// invoice reminder, an invitation to join - never for an account-level one (confirm your email, reset
/// your password), which is about the person, not any Organization. See <see cref="ITenantScopedOptional"/>.
/// </para>
/// <para>
/// <see cref="Id"/> doubles as the <c>RustArchon.Messaging.Contracts.EmailRequested.MessageId</c> for
/// the same send and the tracking-pixel id embedded in <see cref="HtmlBody"/> - one id ties the queued
/// row, the message a Worker instance consumes, the delivery result it reports back, and the pixel hit
/// that marks it viewed all to the same record. See <c>CommunicationPublisher</c>.
/// </para>
/// </remarks>
public class Communication : AuditableEntity, ITenantScopedOptional
{
    /// <summary>The member this went to, when the recipient has an account - see this class's remarks.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The Organization this is about, for an organization-level communication only.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Navigation to <see cref="TenantId"/>.</summary>
    public Tenant? Tenant { get; set; }

    /// <summary>The address it was actually sent to - always populated, unlike <see cref="UserId"/>.</summary>
    [Required]
    [StringLength(320)]
    public string ToAddress { get; set; } = string.Empty;

    [Required]
    [StringLength(500)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// The exact HTML body as sent, tracking pixel included - a permanent copy of what the recipient
    /// actually received, not just a description of it.
    /// </summary>
    [Required]
    public string HtmlBody { get; set; } = string.Empty;

    public CommunicationStatus Status { get; set; } = CommunicationStatus.Queued;

    /// <summary>When this row was created - effectively "when it was queued," but its own column
    /// rather than reusing <see cref="Auditing.IAuditable.CreatedOn"/> so the admin list's "Queue
    /// Date" column reads directly off a name that says what it means.</summary>
    public DateTimeOffset QueuedOn { get; set; }

    public DateTimeOffset? SentOn { get; set; }
    public DateTimeOffset? BouncedOn { get; set; }
    public DateTimeOffset? ViewedOn { get; set; }
    public DateTimeOffset? CancelledOn { get; set; }

    /// <summary>The provider's own error, when <see cref="Status"/> is <see cref="CommunicationStatus.Bounced"/>.</summary>
    [StringLength(2000)]
    public string? FailureReason { get; set; }
}
