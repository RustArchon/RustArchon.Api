// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="RconEvent"/> entities.
/// </summary>
public class RconEventRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<RconEvent>(context, userContext), IRconEventRepository
{
    /// <inheritdoc />
    public async Task<PagedResult<RconEvent>> GetForServerAsync(Guid rustServerId, QueryOptions<RconEvent> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IQueryable<RconEvent> query = _dbSet.Where(e => e.RustServerId == rustServerId);

        // Same sorting/pagination shape as Repository<TEntity>.GetAllAsync - see its remarks - except
        // the sort defaults to newest-first when the caller doesn't specify one, since that's the only
        // sensible default for an event log.
        query = options.SortBy != null
            ? (options.SortDescending ? query.OrderByDescending(options.SortBy) : query.OrderBy(options.SortBy))
            : query.OrderByDescending(e => e.CapturedAtUtc);

        var totalCount = await query.CountAsync();

        if (options.PageNumber.HasValue && options.PageSize.HasValue)
        {
            var pageNumber = options.PageNumber.Value < 1 ? 1 : options.PageNumber.Value;
            var pageSize = options.PageSize.Value < 1 ? 10 : options.PageSize.Value;

            var items = await query.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

            return new PagedResult<RconEvent>
            {
                Items = items,
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
        }

        var allItems = await query.ToListAsync();
        return new PagedResult<RconEvent>
        {
            Items = allItems,
            TotalCount = totalCount,
            PageNumber = 1,
            PageSize = totalCount
        };
    }
}
