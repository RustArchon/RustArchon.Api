// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="ServerReport"/> entities.
/// </summary>
public class ServerReportRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<ServerReport>(context, userContext), IServerReportRepository
{
    /// <inheritdoc />
    public async Task<PagedResult<ServerReport>> GetForServerAsync(
        Guid rustServerId, int pageNumber, int pageSize,
        ServerReportType? type = null, ServerReportStatus? status = null, string? targetSteamId = null)
    {
        IQueryable<ServerReport> query = _dbSet.Where(r => r.RustServerId == rustServerId);

        if (type.HasValue)
        {
            query = query.Where(r => r.Type == type.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        if (!string.IsNullOrEmpty(targetSteamId))
        {
            query = query.Where(r => r.TargetSteamId == targetSteamId);
        }

        query = query.OrderByDescending(r => r.ReceivedAtUtc);

        var totalCount = await query.CountAsync();

        var normalizedPageNumber = pageNumber < 1 ? 1 : pageNumber;
        var normalizedPageSize = pageSize < 1 ? 10 : pageSize;
        var items = await query
            .Skip((normalizedPageNumber - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync();

        return new PagedResult<ServerReport>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = normalizedPageNumber,
            PageSize = normalizedPageSize
        };
    }

    /// <inheritdoc />
    public Task<int> CountNewAsync(Guid rustServerId) =>
        _dbSet.CountAsync(r => r.RustServerId == rustServerId && r.Status == ServerReportStatus.New);

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastNativeReceivedAsync(Guid rustServerId)
    {
        // SQL cannot test a flags column for a bit through EF's enum translation on every provider, so the bit test is
        // spelled as arithmetic that both PostgreSQL and the in-memory test provider evaluate the same way.
        return await _dbSet
            .Where(r => r.RustServerId == rustServerId && ((int)r.Source & (int)ServerReportSource.Native) != 0)
            .OrderByDescending(r => r.ReceivedAtUtc)
            .Select(r => (DateTimeOffset?)r.ReceivedAtUtc)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<ServerReport?> FindMergeCandidateAcrossTenantsAsync(
        Guid rustServerId, string? reporterSteamId, ServerReportType type, string subject,
        DateTimeOffset since, ServerReportSource incomingSource)
    {
        // Without a reporter there is nothing to say two reports are the same person's, so nothing merges.
        if (string.IsNullOrEmpty(reporterSteamId))
        {
            return null;
        }

        var incoming = (int)incomingSource;
        return await _dbSet
            .AcrossAllTenants()
            .Where(r => r.RustServerId == rustServerId
                && r.ReporterSteamId == reporterSteamId
                && r.Type == type
                && r.Subject == subject
                && r.ReceivedAtUtc >= since
                && ((int)r.Source & incoming) == 0)
            .OrderByDescending(r => r.ReceivedAtUtc)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<int> DeleteForServerAcrossTenantsAsync(Guid rustServerId)
    {
        var rows = await _dbSet.AcrossAllTenants().Where(r => r.RustServerId == rustServerId).ToListAsync();
        if (rows.Count == 0)
        {
            return 0;
        }

        _dbSet.RemoveRange(rows);
        await _context.SaveChangesAsync();
        return rows.Count;
    }
}
