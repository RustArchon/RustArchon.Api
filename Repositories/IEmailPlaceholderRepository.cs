// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>Repository interface for <see cref="EmailPlaceholder"/> entities.</summary>
public interface IEmailPlaceholderRepository : IRepository<EmailPlaceholder>
{
    /// <summary>Gets a placeholder by its unique <see cref="EmailPlaceholder.Name"/> - the lookup
    /// every reader (the registry's idempotency check, <c>EmailPlaceholdersController</c>,
    /// <c>EmailTemplatesController.UpdatePlaceholders</c>) actually needs.</summary>
    Task<EmailPlaceholder?> GetByNameAsync(string name);
}
