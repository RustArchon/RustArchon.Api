// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>Repository implementation for <see cref="EmailPlaceholder"/> entities.</summary>
public class EmailPlaceholderRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<EmailPlaceholder>(context, userContext), IEmailPlaceholderRepository
{
    /// <inheritdoc />
    public Task<EmailPlaceholder?> GetByNameAsync(string name) =>
        _dbSet.FirstOrDefaultAsync(p => p.Name == name);
}
