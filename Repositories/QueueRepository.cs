// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <inheritdoc cref="IQueueRepository" />
public class QueueRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<Queue>(context, userContext), IQueueRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Queue>> GetActiveAsync(CancellationToken cancellationToken = default) =>
        await _dbSet
            .Where(queue => queue.IsActive)
            .OrderBy(queue => queue.DisplayOrder)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Queue?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        _dbSet.FirstOrDefaultAsync(queue => queue.Slug == slug, cancellationToken);
}
