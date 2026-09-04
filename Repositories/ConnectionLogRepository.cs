// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="ConnectionLogEntry"/> entities.
/// </summary>
public class ConnectionLogRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<ConnectionLogEntry>(context, userContext), IConnectionLogRepository
{
    /// <inheritdoc />
    public Task<List<ConnectionLogEntry>> GetForServerAsync(Guid rustServerId, DateTimeOffset since, DateTimeOffset until) =>
        _dbSet
            .Where(e => e.RustServerId == rustServerId && e.OccurredAtUtc >= since && e.OccurredAtUtc <= until)
            .OrderByDescending(e => e.OccurredAtUtc)
            .ToListAsync();
}
