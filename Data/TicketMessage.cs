// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using JumpStart.Data.Auditing;

namespace RustArchon.Api.Data;

/// <summary>Who wrote a <see cref="TicketMessage"/>.</summary>
public enum TicketMessageAuthorType
{
    /// <summary>The submitter - a signed-in tenant user, or an anonymous guest (see
    /// <see cref="TicketMessage.AuthorUserId"/>).</summary>
    Customer,

    /// <summary>A site admin working the ticket.</summary>
    Staff
}

/// <summary>
/// One message in a <see cref="Ticket"/>'s customer-visible thread - the two-way conversation between
/// the submitter and staff. Distinct from <see cref="TicketNote"/>, which the submitter never sees.
/// </summary>
public class TicketMessage : AuditableEntity
{
    public Guid TicketId { get; set; }

    public Ticket Ticket { get; set; } = null!;

    public TicketMessageAuthorType AuthorType { get; set; }

    /// <summary>
    /// The staff member or signed-in tenant user who wrote this, or <c>null</c> when
    /// <see cref="AuthorType"/> is <see cref="TicketMessageAuthorType.Customer"/> and the ticket has
    /// no <see cref="Ticket.SubmitterUserId"/> yet - an anonymous guest reply.
    /// </summary>
    public Guid? AuthorUserId { get; set; }

    [Required]
    [StringLength(4000)]
    public string Body { get; set; } = string.Empty;
}
