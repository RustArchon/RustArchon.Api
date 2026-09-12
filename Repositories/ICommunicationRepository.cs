// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository for <see cref="Communication"/> - a permanent, admin-visible record of outbound email,
/// read and written across the tenant boundary on purpose. See <see cref="NoteRepository"/>'s remarks
/// for why the inherited <see cref="IRepository{TEntity}"/> members can't be reused as written for
/// this kind of entity - the same reasoning applies here.
/// </summary>
public interface ICommunicationRepository : IRepository<Communication>
{
    /// <summary>Communications about <paramref name="tenantId"/> and/or <paramref name="userId"/>,
    /// newest first. At least one is required - see <c>CommunicationsController.List</c>.</summary>
    Task<IReadOnlyList<Communication>> GetVisibleAsync(
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>The communication, from any tenant, or <c>null</c> if it doesn't exist.</summary>
    Task<Communication?> GetByIdAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves changes already made to a communication fetched via
    /// <see cref="GetByIdAcrossTenantsAsync"/> - every status transition (sent, bounced, viewed,
    /// cancelled) goes through this rather than the inherited <c>UpdateAsync</c>, for the same
    /// tenant-filtered-refetch reason <see cref="NoteRepository.SaveAsync"/> exists.
    /// </summary>
    Task SaveAsync(Communication communication, CancellationToken cancellationToken = default);
}
