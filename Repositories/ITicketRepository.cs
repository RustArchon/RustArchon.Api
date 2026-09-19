// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository for <see cref="Ticket"/> and its <see cref="TicketMessage"/>/<see cref="TicketNote"/>
/// children.
/// </summary>
/// <remarks>
/// A site admin working the staff console acts across every tenant - see
/// <see cref="INoteRepository"/>'s remarks for why the inherited, ambient-tenant-filtered
/// <see cref="IRepository{TEntity}"/> members can't serve that. <see cref="GetForTenantAsync"/>
/// deliberately does NOT reuse <see cref="Ticket"/>'s own <c>ITenantScopedOptional</c> query filter
/// either: that filter's "visible if global OR belongs to the current tenant" rule is right for
/// <see cref="Note"/>/<see cref="Communication"/>, where an untenanted row is a deliberate
/// platform-wide fact every tenant should see, but wrong here - an untenanted <see cref="Ticket"/> is
/// an anonymous prospect's submission, unrelated to any tenant, and must never show up in one tenant's
/// own "my tickets" list just because it has no tenant of its own.
/// </remarks>
public interface ITicketRepository : IRepository<Ticket>
{
    /// <summary>The ticket, from any tenant (including none), or <c>null</c> if it doesn't exist.</summary>
    Task<Ticket?> GetByIdAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The ticket with an unexpired, matching <see cref="Ticket.GuestAccessToken"/>, or
    /// <c>null</c> if the token doesn't match anything or has expired.</summary>
    Task<Ticket?> GetByGuestAccessTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Every ticket belonging to exactly this tenant - never an untenanted (prospect) ticket,
    /// even though this tenant's ambient query filter would otherwise let one through. See this
    /// interface's remarks.</summary>
    Task<IReadOnlyList<Ticket>> GetForTenantAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every ticket across every tenant (and every untenanted one), for the staff console - optionally
    /// narrowed to one queue and/or one status. When <paramref name="statusId"/> is <c>null</c>,
    /// <paramref name="isClosed"/> narrows by the coarser <see cref="TicketStatus.IsClosed"/> bucket
    /// instead - <c>false</c> for the "Open" filter, <c>true</c> for "Closed", <c>null</c> for every
    /// status regardless. Naming a specific <paramref name="statusId"/> always returns tickets in it
    /// regardless of <paramref name="isClosed"/>, since choosing one status by name is a more specific
    /// ask than either bucket.
    /// </summary>
    Task<IReadOnlyList<Ticket>> GetForStaffAsync(
        Guid? queueId, Guid? statusId, bool? isClosed, CancellationToken cancellationToken = default);

    /// <summary>Saves changes already made to a ticket fetched via one of this interface's own
    /// lookups, stamping <c>ModifiedOn</c>/<c>ModifiedById</c> the same way
    /// <see cref="INoteRepository.SaveAsync"/> does.</summary>
    Task SaveAsync(Ticket ticket, CancellationToken cancellationToken = default);

    /// <summary>A ticket's customer-visible thread, oldest first.</summary>
    Task<IReadOnlyList<TicketMessage>> GetMessagesAsync(
        Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>Appends a message to a ticket's thread.</summary>
    Task<TicketMessage> AddMessageAsync(TicketMessage message, CancellationToken cancellationToken = default);

    /// <summary>A ticket's staff-only notes, oldest first. Never call this to build a customer-facing
    /// response - see <see cref="TicketNote"/>'s remarks.</summary>
    Task<IReadOnlyList<TicketNote>> GetNotesAsync(
        Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>Appends an internal note to a ticket.</summary>
    Task<TicketNote> AddNoteAsync(TicketNote note, CancellationToken cancellationToken = default);
}
