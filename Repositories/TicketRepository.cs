// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <inheritdoc cref="ITicketRepository" />
public class TicketRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<Ticket>(context, userContext), ITicketRepository
{
    /// <inheritdoc />
    public Task<Ticket?> GetByIdAcrossTenantsAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbSet.AcrossAllTenants().Include(ticket => ticket.Queue).Include(ticket => ticket.Status)
            .FirstOrDefaultAsync(ticket => ticket.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Ticket?> GetByGuestAccessTokenAsync(string token, CancellationToken cancellationToken = default) =>
        _dbSet.AcrossAllTenants().Include(ticket => ticket.Queue).Include(ticket => ticket.Status).FirstOrDefaultAsync(
            ticket => ticket.GuestAccessToken == token
                && ticket.GuestAccessTokenExpiresOn != null
                && ticket.GuestAccessTokenExpiresOn > DateTimeOffset.UtcNow,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Ticket>> GetForTenantAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        await _dbSet.AcrossAllTenants().Include(ticket => ticket.Queue).Include(ticket => ticket.Status)
            .Where(ticket => ticket.TenantId == tenantId)
            .OrderByDescending(ticket => ticket.SubmittedOn)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Ticket>> GetForStaffAsync(
        Guid? queueId, Guid? statusId, bool? isClosed, CancellationToken cancellationToken = default)
    {
        IQueryable<Ticket> query = _dbSet.AcrossAllTenants().Include(ticket => ticket.Queue).Include(ticket => ticket.Status);

        if (queueId.HasValue)
        {
            query = query.Where(ticket => ticket.QueueId == queueId.Value);
        }

        if (statusId.HasValue)
        {
            query = query.Where(ticket => ticket.StatusId == statusId.Value);
        }
        else if (isClosed.HasValue)
        {
            query = query.Where(ticket => ticket.Status.IsClosed == isClosed.Value);
        }

        return await query.OrderByDescending(ticket => ticket.SubmittedOn).ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        ticket.ModifiedOn = DateTimeOffset.UtcNow;

        if (_userContext is not null && await _userContext.GetCurrentUserIdAsync() is { } userId)
        {
            ticket.ModifiedById = userId;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketMessage>> GetMessagesAsync(
        Guid ticketId, CancellationToken cancellationToken = default) =>
        await context.Set<TicketMessage>()
            .Where(message => message.TicketId == ticketId)
            .OrderBy(message => message.CreatedOn)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<TicketMessage> AddMessageAsync(
        TicketMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        message.CreatedOn = DateTimeOffset.UtcNow;

        if (_userContext is not null && await _userContext.GetCurrentUserIdAsync() is { } userId)
        {
            message.CreatedById = userId;
        }

        context.Set<TicketMessage>().Add(message);
        await context.SaveChangesAsync(cancellationToken);
        return message;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketNote>> GetNotesAsync(
        Guid ticketId, CancellationToken cancellationToken = default) =>
        await context.Set<TicketNote>()
            .Where(note => note.TicketId == ticketId)
            .OrderBy(note => note.CreatedOn)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<TicketNote> AddNoteAsync(TicketNote note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        note.CreatedOn = DateTimeOffset.UtcNow;

        if (_userContext is not null && await _userContext.GetCurrentUserIdAsync() is { } userId)
        {
            note.CreatedById = userId;
        }

        context.Set<TicketNote>().Add(note);
        await context.SaveChangesAsync(cancellationToken);
        return note;
    }
}
