// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="PlayerSession"/> entities.
/// </summary>
public class PlayerSessionRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PlayerSession>(context, userContext), IPlayerSessionRepository
{
    /// <inheritdoc />
    public async Task<PagedResult<PlayerSession>> GetForServerAsync(
        Guid rustServerId, QueryOptions<PlayerSession> options, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        IQueryable<PlayerSession> query = _dbSet.Where(s => s.RustServerId == rustServerId);

        if (since.HasValue)
        {
            query = query.Where(s => s.ConnectedAtUtc >= since.Value);
        }

        if (until.HasValue)
        {
            query = query.Where(s => s.ConnectedAtUtc <= until.Value);
        }

        query = options.SortBy != null
            ? (options.SortDescending ? query.OrderByDescending(options.SortBy) : query.OrderBy(options.SortBy))
            : query.OrderByDescending(s => s.ConnectedAtUtc);

        var totalCount = await query.CountAsync();

        if (options.PageNumber.HasValue && options.PageSize.HasValue)
        {
            var pageNumber = options.PageNumber.Value < 1 ? 1 : options.PageNumber.Value;
            var pageSize = options.PageSize.Value < 1 ? 10 : options.PageSize.Value;

            var items = await query.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

            return new PagedResult<PlayerSession>
            {
                Items = items,
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
        }

        var allItems = await query.ToListAsync();
        return new PagedResult<PlayerSession>
        {
            Items = allItems,
            TotalCount = totalCount,
            PageNumber = 1,
            PageSize = totalCount
        };
    }

    /// <inheritdoc />
    public Task<List<PlayerSession>> GetCurrentlyConnectedAsync(Guid rustServerId) =>
        _dbSet.Where(s => s.RustServerId == rustServerId && s.DisconnectedAtUtc == null)
              .OrderBy(s => s.DisplayName)
              .ToListAsync();

    /// <inheritdoc />
    public Task<PlayerSession?> GetOpenSessionAsync(Guid rustServerId, string steamId) =>
        _dbSet.Where(s => s.RustServerId == rustServerId && s.SteamId == steamId && s.DisconnectedAtUtc == null)
              .OrderByDescending(s => s.ConnectedAtUtc)
              .FirstOrDefaultAsync();

    /// <inheritdoc />
    public Task<List<PlayerSession>> GetOpenSessionsAsync(Guid rustServerId, string steamId) =>
        _dbSet.Where(s => s.RustServerId == rustServerId && s.SteamId == steamId && s.DisconnectedAtUtc == null)
              .ToListAsync();

    /// <inheritdoc />
    public async Task<PagedResult<InactivePlayerDto>> GetInactivePlayersAsync(Guid rustServerId, int pageNumber, int pageSize)
    {
        var openSteamIds = await _dbSet
            .Where(s => s.RustServerId == rustServerId && s.DisconnectedAtUtc == null)
            .Select(s => s.SteamId)
            .ToListAsync();

        var allSessions = await _dbSet
            .Where(s => s.RustServerId == rustServerId && !openSteamIds.Contains(s.SteamId))
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        var summaries = allSessions
            .GroupBy(s => s.SteamId)
            .Select(group =>
            {
                var latest = group.OrderByDescending(s => s.ConnectedAtUtc).First();
                var totalOnServer = TimeSpan.FromTicks(
                    group.Sum(s => ((s.DisconnectedAtUtc ?? now) - s.ConnectedAtUtc).Ticks));

                return new InactivePlayerDto
                {
                    SteamId = group.Key,
                    DisplayName = latest.DisplayName,
                    Country = latest.GeolocationCountry,
                    CountryCode = latest.GeolocationCountryCode,
                    IsVpn = latest.GeolocationIsVpn,
                    VacBanned = latest.SteamVacBanned,
                    NumberOfVacBans = latest.SteamNumberOfVacBans,
                    NumberOfGameBans = latest.SteamNumberOfGameBans,
                    SteamMinutesPlayedForever = latest.SteamMinutesPlayedForever,
                    HoursOnServer = totalOnServer,
                    LastPing = latest.LastPing,
                    LastViolationLevel = latest.LastViolationLevel,
                    IpAddress = latest.IpAddress,
                    LastConnectedAtUtc = latest.ConnectedAtUtc,
                    LastDisconnectedAtUtc = latest.DisconnectedAtUtc,
                    // Every row here is excluded from openSteamIds above, so the latest session for
                    // each group is guaranteed already closed - this is never null in practice.
                    LastConnectionDuration = (latest.DisconnectedAtUtc ?? now) - latest.ConnectedAtUtc
                };
            })
            .OrderByDescending(s => s.LastConnectedAtUtc)
            .ToList();

        var totalCount = summaries.Count;
        var normalizedPageNumber = pageNumber < 1 ? 1 : pageNumber;
        var normalizedPageSize = pageSize < 1 ? 10 : pageSize;
        var page = summaries
            .Skip((normalizedPageNumber - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToList();

        return new PagedResult<InactivePlayerDto>
        {
            Items = page,
            TotalCount = totalCount,
            PageNumber = normalizedPageNumber,
            PageSize = normalizedPageSize
        };
    }

    /// <inheritdoc />
    public async Task<PlayerDetailDto?> GetPlayerDetailAsync(Guid rustServerId, string steamId)
    {
        var sessions = await _dbSet
            .Where(s => s.RustServerId == rustServerId && s.SteamId == steamId)
            .ToListAsync();

        if (sessions.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var latest = sessions.OrderByDescending(s => s.ConnectedAtUtc).First();
        var openSession = sessions.FirstOrDefault(s => s.DisconnectedAtUtc == null);
        var totalOnServer = TimeSpan.FromTicks(sessions.Sum(s => ((s.DisconnectedAtUtc ?? now) - s.ConnectedAtUtc).Ticks));

        return new PlayerDetailDto
        {
            SteamId = steamId,
            DisplayName = latest.DisplayName,
            IsCurrentlyConnected = openSession is not null,
            CurrentSessionConnectedAtUtc = openSession?.ConnectedAtUtc,
            Country = latest.GeolocationCountry,
            CountryCode = latest.GeolocationCountryCode,
            IsVpn = latest.GeolocationIsVpn,
            VacBanned = latest.SteamVacBanned,
            NumberOfVacBans = latest.SteamNumberOfVacBans,
            NumberOfGameBans = latest.SteamNumberOfGameBans,
            SteamMinutesPlayedForever = latest.SteamMinutesPlayedForever,
            HoursOnServer = totalOnServer,
            SessionCount = sessions.Count,
            FirstConnectedAtUtc = sessions.Min(s => s.ConnectedAtUtc),
            LastConnectedAtUtc = latest.ConnectedAtUtc
        };
    }

    /// <inheritdoc />
    public async Task<PagedResult<PlayerSession>> GetSessionsForPlayerAsync(
        Guid rustServerId, string steamId, int pageNumber, int pageSize)
    {
        var query = _dbSet
            .Where(s => s.RustServerId == rustServerId && s.SteamId == steamId)
            .OrderByDescending(s => s.ConnectedAtUtc);

        var totalCount = await query.CountAsync();

        var normalizedPageNumber = pageNumber < 1 ? 1 : pageNumber;
        var normalizedPageSize = pageSize < 1 ? 10 : pageSize;
        var items = await query
            .Skip((normalizedPageNumber - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync();

        return new PagedResult<PlayerSession>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = normalizedPageNumber,
            PageSize = normalizedPageSize
        };
    }
}
