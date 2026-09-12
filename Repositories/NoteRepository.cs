// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <inheritdoc cref="INoteRepository" />
/// <remarks>
/// <para>
/// <see cref="Repository{TEntity}.UpdateAsync"/> and <see cref="Repository{TEntity}.DeleteAsync"/>
/// both re-fetch the row by id through <c>DbSet.FindAsync</c> before acting on it, and
/// <c>FindAsync</c> honours the ambient tenant filter the same as any other query - so calling either
/// one for a note outside the caller's own tenant throws "not found" for a row that plainly exists.
/// <see cref="SaveAsync"/> and <see cref="DeleteAcrossTenantsAsync"/> exist because of that, not out
/// of a preference for hand-rolled persistence: they do the same audit-stamping the base class does,
/// against a row already fetched with <c>AcrossAllTenants()</c>.
/// </para>
/// </remarks>
public class NoteRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<Note>(context, userContext), INoteRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Note>> GetVisibleAsync(
        Guid? tenantId, Guid? userId, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AcrossAllTenants();

        if (tenantId.HasValue)
        {
            query = query.Where(note => note.TenantId == tenantId.Value);
        }

        if (userId.HasValue)
        {
            query = query.Where(note => note.UserId == userId.Value);
        }

        return await query
            .Where(note => !note.IsPrivate || note.CreatedById == currentUserId)
            .OrderByDescending(note => note.CreatedOn)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<Note?> GetByIdAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbSet.AcrossAllTenants().FirstOrDefaultAsync(note => note.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task SaveAsync(Note note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        note.ModifiedOn = DateTimeOffset.UtcNow;

        if (_userContext is not null && await _userContext.GetCurrentUserIdAsync() is { } userId)
        {
            note.ModifiedById = userId;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var note = await GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (note is null)
        {
            return false;
        }

        note.DeletedOn = DateTimeOffset.UtcNow;

        if (_userContext is not null && await _userContext.GetCurrentUserIdAsync() is { } userId)
        {
            note.DeletedById = userId;
        }

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
