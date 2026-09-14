// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <inheritdoc cref="IThemeRepository" />
public class ThemeRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<Theme>(context, userContext), IThemeRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Theme>> GetAllOrderedAsync(CancellationToken cancellationToken = default) =>
        await _dbSet.OrderByDescending(t => t.CreatedOn).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Theme?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        _dbSet.FirstOrDefaultAsync(t => t.IsActive, cancellationToken);
}
