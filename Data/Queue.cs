// Copyright ©2026 Scott Blomfield

using System.ComponentModel.DataAnnotations;
using JumpStart.Data.Auditing;

namespace RustArchon.Api.Data;

/// <summary>
/// A named bucket a <see cref="Ticket"/> is routed into - "Support," "Pre-Sales," "Bug Reports."
/// Platform-wide, not tenant-scoped: every queue is visible to every site admin, since the whole
/// point is one shared set of buckets the staff console filters by.
/// </summary>
/// <remarks>
/// Admin-managed master data, same shape as <see cref="Plan"/> - see <c>QueueSeeder</c> for the
/// starter set a fresh deployment gets. <see cref="Slug"/>, not <see cref="AuditableNamedEntity.Name"/>,
/// is what code refers to a well-known queue by (e.g. which one a fresh <see cref="Ticket"/> defaults
/// into), since an admin renaming a queue for display shouldn't be able to silently break routing.
/// </remarks>
public class Queue : AuditableNamedEntity
{
    /// <summary>Stable, code-referenceable key - see this class's remarks. Never shown to a user.</summary>
    [Required]
    [StringLength(64)]
    public string Slug { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this is the generic intake queue a <see cref="Ticket"/> lands in when nothing more
    /// specific applies. Exactly one queue should have this set; <c>QueueSeeder</c> and the admin UI
    /// are both responsible for keeping that true - not enforced by the database.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Whether this queue can still be routed into. An inactive queue is hidden from every picker but
    /// its existing <see cref="Ticket"/> rows keep pointing at it - retiring a queue never orphans
    /// ticket history the way deleting the row would.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public int DisplayOrder { get; set; }
}
