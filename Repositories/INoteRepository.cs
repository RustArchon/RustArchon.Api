// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository for <see cref="Note"/> - a site admin's own annotations on Organizations and people.
/// </summary>
/// <remarks>
/// Every method here reads or writes across the tenant boundary on purpose. A site admin annotating a
/// customer's Organization is never acting inside their own ambient tenant, so the inherited
/// <see cref="IRepository{TEntity}"/> members - filtered to whichever tenant issued the caller's token
/// - would either see nothing or, for <c>UpdateAsync</c>/<c>DeleteAsync</c>, fail to find the row at
/// all. See <see cref="NoteRepository"/>'s remarks for why those two specifically cannot be reused as
/// written.
/// </remarks>
public interface INoteRepository : IRepository<Note>
{
    /// <summary>
    /// Notes about <paramref name="tenantId"/> and/or <paramref name="userId"/>, newest first, minus
    /// whatever <paramref name="currentUserId"/> is not allowed to see.
    /// </summary>
    /// <remarks>
    /// A private note is filtered out here rather than merely hidden in the UI - the whole point of
    /// <see cref="Note.IsPrivate"/> is that another admin never receives it at all.
    /// </remarks>
    Task<IReadOnlyList<Note>> GetVisibleAsync(
        Guid? tenantId, Guid? userId, Guid? currentUserId, CancellationToken cancellationToken = default);

    /// <summary>The note, from any tenant, or <c>null</c> if it doesn't exist.</summary>
    Task<Note?> GetByIdAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves changes already made to a note fetched via <see cref="GetByIdAcrossTenantsAsync"/>,
    /// stamping <see cref="JumpStart.Data.Auditing.IModifiable.ModifiedOn"/> and
    /// <see cref="JumpStart.Data.Auditing.IModifiable.ModifiedById"/>.
    /// </summary>
    Task SaveAsync(Note note, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a note from any tenant. <c>false</c> if it doesn't exist.</summary>
    Task<bool> DeleteAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default);
}
