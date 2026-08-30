// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="RustServer"/> entities.
/// </summary>
public class RustServerRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<RustServer>(context, userContext), IRustServerRepository
{
    /// <inheritdoc />
    public async Task<RustServer?> GetByNameAsync(string name)
    {
        return await _dbSet.FirstOrDefaultAsync(server => server.Name == name);
    }
}
