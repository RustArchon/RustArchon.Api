// Copyright ©2026 Scott Blomfield

using System.ComponentModel.DataAnnotations;
using JumpStart.Data.Auditing;

namespace RustArchon.Api.Data;

/// <summary>
/// A state a <see cref="Ticket"/> can be in - "Open," "Resolved," "Cancelled." Admin-managed master
/// data, the same shape as <see cref="Queue"/>: <see cref="Slug"/>, not
/// <see cref="AuditableNamedEntity.Name"/>, is what code refers to a well-known status by, so a site
/// admin renaming "Waiting on Customer" to "Awaiting Reply" (say) can't silently break the reopen
/// logic in <c>TicketsController.AddMessage</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsProtected"/> marks every one of the seven starter statuses <c>TicketStatusSeeder</c>
/// creates - most of them because ticket-lifecycle code depends on them by slug, but <c>Cancelled</c>
/// is protected too even though no code ever assigns it, simply because it's meant to be a permanent
/// part of the lifecycle rather than something an admin could delete out from under old ticket history.
/// A protected status can still be renamed, reordered, or have <see cref="IsClosed"/> flipped (that's
/// the whole point of making this an entity instead of an enum - a self-hoster's workflow decides what
/// "closed" means), but it can never be deleted or deactivated.
/// </para>
/// <para>
/// <see cref="IsClosed"/> is what <c>AdminTicketsController.List</c>'s default queue view hides -
/// added because an admin's working queue got cluttered with tickets nobody needed to look at again.
/// It's independent of <see cref="IsProtected"/>: <c>Resolved</c> is protected but not closed (a
/// resolved ticket can still reopen), while <c>Closed</c> and <c>Cancelled</c> are both closed and both
/// protected.
/// </para>
/// </remarks>
public class TicketStatus : AuditableNamedEntity
{
    /// <summary>Stable, code-referenceable key - see this class's remarks. Never shown to a user.</summary>
    [Required]
    [StringLength(64)]
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Whether a <see cref="Ticket"/> in this status is done - hidden from the staff console's default
    /// queue view. Freely editable, including on a protected status - see this class's remarks.
    /// </summary>
    public bool IsClosed { get; set; }

    /// <summary>
    /// Whether ticket-lifecycle code looks this status up by <see cref="Slug"/> - see this class's
    /// remarks. Set only by <c>TicketStatusSeeder</c>, never by the admin UI.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>
    /// Whether this status can still be assigned to a ticket. An inactive status is hidden from every
    /// picker but any <see cref="Ticket"/> already in it keeps pointing at it - retiring a status never
    /// orphans ticket history the way deleting the row would. A protected status can never be
    /// deactivated, for the same reason it can never be deleted.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public int DisplayOrder { get; set; }
}
