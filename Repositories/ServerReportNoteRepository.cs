// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="ServerReportNote"/> entities.
/// </summary>
public class ServerReportNoteRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<ServerReportNote>(context, userContext), IServerReportNoteRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ServerReportNote>> GetForReportAsync(Guid serverReportId) =>
        await _dbSet
            .Where(n => n.ServerReportId == serverReportId)
            .OrderBy(n => n.CreatedOn)
            .ToListAsync();
}
