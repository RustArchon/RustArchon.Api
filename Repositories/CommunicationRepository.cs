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

/// <inheritdoc cref="ICommunicationRepository" />
/// <remarks>
/// See <see cref="NoteRepository"/>'s own remarks for why <see cref="SaveAsync"/> exists rather than
/// the inherited <c>UpdateAsync</c> - the same tenant-filtered-refetch gap applies here. Creation goes
/// through the inherited <c>AddAsync</c> unmodified: only a status transition on an existing row needs
/// the cross-tenant path.
/// </remarks>
public class CommunicationRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<Communication>(context, userContext), ICommunicationRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Communication>> GetVisibleAsync(
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AcrossAllTenants();

        if (tenantId.HasValue)
        {
            query = query.Where(c => c.TenantId == tenantId.Value);
        }

        if (userId.HasValue)
        {
            query = query.Where(c => c.UserId == userId.Value);
        }

        return await query
            .OrderByDescending(c => c.QueuedOn)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<Communication?> GetByIdAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbSet.AcrossAllTenants().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task SaveAsync(Communication communication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(communication);

        communication.ModifiedOn = DateTimeOffset.UtcNow;

        if (_userContext is not null && await _userContext.GetCurrentUserIdAsync() is { } userId)
        {
            communication.ModifiedById = userId;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
