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

/// <inheritdoc cref="ITicketStatusRepository" />
public class TicketStatusRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<TicketStatus>(context, userContext), ITicketStatusRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketStatus>> GetAllOrderedAsync(CancellationToken cancellationToken = default) =>
        await _dbSet.OrderBy(status => status.DisplayOrder).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketStatus>> GetActiveAsync(CancellationToken cancellationToken = default) =>
        await _dbSet
            .Where(status => status.IsActive)
            .OrderBy(status => status.DisplayOrder)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<TicketStatus?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        _dbSet.FirstOrDefaultAsync(status => status.Slug == slug, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsInUseAsync(Guid statusId, CancellationToken cancellationToken = default) =>
        context.Set<Ticket>().AcrossAllTenants().AnyAsync(ticket => ticket.StatusId == statusId, cancellationToken);
}
