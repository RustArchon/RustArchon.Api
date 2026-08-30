// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="RustServer"/> entities.
/// </summary>
public interface IRustServerRepository : IRepository<RustServer>
{
    /// <summary>
    /// Finds a server by name within the current tenant. Names are unique per tenant.
    /// </summary>
    Task<RustServer?> GetByNameAsync(string name);
}
