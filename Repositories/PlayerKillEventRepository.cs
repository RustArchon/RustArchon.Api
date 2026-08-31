// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="PlayerKillEvent"/> entities.
/// </summary>
public class PlayerKillEventRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PlayerKillEvent>(context, userContext), IPlayerKillEventRepository
{
    /// <inheritdoc />
    public async Task<PagedResult<PlayerKillEvent>> GetForServerAsync(
        Guid rustServerId, QueryOptions<PlayerKillEvent> options, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        IQueryable<PlayerKillEvent> query = _dbSet.Where(k => k.RustServerId == rustServerId);

        if (since.HasValue)
        {
            query = query.Where(k => k.OccurredAtUtc >= since.Value);
        }

        if (until.HasValue)
        {
            query = query.Where(k => k.OccurredAtUtc <= until.Value);
        }

        query = options.SortBy != null
            ? (options.SortDescending ? query.OrderByDescending(options.SortBy) : query.OrderBy(options.SortBy))
            : query.OrderByDescending(k => k.OccurredAtUtc);

        var totalCount = await query.CountAsync();

        if (options.PageNumber.HasValue && options.PageSize.HasValue)
        {
            var pageNumber = options.PageNumber.Value < 1 ? 1 : options.PageNumber.Value;
            var pageSize = options.PageSize.Value < 1 ? 10 : options.PageSize.Value;

            var items = await query.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

            return new PagedResult<PlayerKillEvent>
            {
                Items = items,
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
        }

        var allItems = await query.ToListAsync();
        return new PagedResult<PlayerKillEvent>
        {
            Items = allItems,
            TotalCount = totalCount,
            PageNumber = 1,
            PageSize = totalCount
        };
    }

    /// <inheritdoc />
    public async Task<PagedResult<PlayerKillEvent>> GetForPlayerAsync(
        Guid rustServerId, string steamId, int pageNumber, int pageSize)
    {
        var query = _dbSet
            .Where(k => k.RustServerId == rustServerId && (k.VictimSteamId == steamId || k.KillerSteamId == steamId))
            .OrderByDescending(k => k.OccurredAtUtc);

        var totalCount = await query.CountAsync();

        var normalizedPageNumber = pageNumber < 1 ? 1 : pageNumber;
        var normalizedPageSize = pageSize < 1 ? 10 : pageSize;
        var items = await query
            .Skip((normalizedPageNumber - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync();

        return new PagedResult<PlayerKillEvent>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = normalizedPageNumber,
            PageSize = normalizedPageSize
        };
    }
}
