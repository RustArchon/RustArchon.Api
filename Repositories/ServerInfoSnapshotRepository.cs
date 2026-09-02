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
/// Repository implementation for <see cref="ServerInfoSnapshot"/> entities.
/// </summary>
public class ServerInfoSnapshotRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<ServerInfoSnapshot>(context, userContext), IServerInfoSnapshotRepository
{
    /// <inheritdoc />
    public Task<List<ServerInfoSnapshot>> GetForServerAsync(Guid rustServerId, DateTimeOffset since, DateTimeOffset until) =>
        _dbSet
            .Where(s => s.RustServerId == rustServerId && s.CapturedAtUtc >= since && s.CapturedAtUtc <= until)
            .OrderBy(s => s.CapturedAtUtc)
            .ToListAsync();
}
