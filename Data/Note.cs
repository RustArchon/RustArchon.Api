// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using JumpStart.Data;
using JumpStart.Data.Auditing;
using JumpStart.Data.MultiTenant;

namespace RustArchon.Api.Data;

/// <summary>
/// A site admin's internal annotation about an Organization, a person, or both.
/// </summary>
/// <remarks>
/// <para>
/// Not a customer-facing feature - there is no screen where a tenant's own members see these. It is
/// the same idea as a CRM's account notes: somewhere for "called them about the overdue invoice on
/// the 3rd, they're paying Friday" to live, attached to the account or the person it's about, visible
/// to whoever administers the platform next.
/// </para>
/// <para>
/// <see cref="TenantId"/> and <see cref="UserId"/> are both optional, but a note naming neither is
/// meaningless - the controller refuses to create one. Naming both is normal: "asked to be added to
/// Acme Corp, invited" is a note about a specific person's request to a specific Organization.
/// </para>
/// </remarks>
public class Note : AuditableEntity, ITenantScopedOptional
{
    /// <summary>The Organization this note is about, or <c>null</c> if it is about a person only.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Navigation to <see cref="TenantId"/>.</summary>
    public Tenant? Tenant { get; set; }

    /// <summary>The person this note is about, or <c>null</c> if it is about an Organization only.</summary>
    public Guid? UserId { get; set; }

    [StringLength(256)]
    public string? Title { get; set; }

    [Required]
    [StringLength(4000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Whether only <see cref="Auditing.IAuditable.CreatedById"/> may read, edit or delete this note.
    /// </summary>
    /// <remarks>
    /// A public note is a shared record any site admin may act on; a private one is the author's own
    /// working note - a half-formed suspicion not yet worth another admin seeing, say. Enforced by
    /// <c>NotesController</c>, not by the query filter: a private note is still an Organization-wide
    /// or platform-wide fact for authorization purposes, just not one every admin gets to read.
    /// </remarks>
    public bool IsPrivate { get; set; }
}
