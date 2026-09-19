// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using JumpStart.Data.Auditing;

namespace RustArchon.Api.Data;

/// <summary>
/// A site admin's internal annotation on a <see cref="Ticket"/> - never rendered to the submitter,
/// enforced by <c>TicketsController</c>, not by the database. Same idea as <see cref="Note"/>, scoped
/// to one ticket instead of an Organization or a person.
/// </summary>
public class TicketNote : AuditableEntity
{
    public Guid TicketId { get; set; }

    public Ticket Ticket { get; set; } = null!;

    /// <summary>Which staff member wrote this - always required, unlike <see cref="Note.UserId"/>,
    /// which names who a <see cref="Note"/> is about rather than who wrote it.</summary>
    public Guid AuthorUserId { get; set; }

    [Required]
    [StringLength(4000)]
    public string Content { get; set; } = string.Empty;
}
