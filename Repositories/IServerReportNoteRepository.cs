// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="ServerReportNote"/> entities.
/// </summary>
public interface IServerReportNoteRepository : IRepository<ServerReportNote>
{
    /// <summary>One report's notes, oldest first, tenant-scoped like every other query on this entity.</summary>
    Task<IReadOnlyList<ServerReportNote>> GetForReportAsync(Guid serverReportId);
}
