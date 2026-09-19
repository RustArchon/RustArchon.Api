// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>Repository for <see cref="TicketStatus"/> - the states a <see cref="Ticket"/> can be in.</summary>
public interface ITicketStatusRepository : IRepository<TicketStatus>
{
    /// <summary>Every status, active or not, in display order - what the admin management page shows.</summary>
    Task<IReadOnlyList<TicketStatus>> GetAllOrderedAsync(CancellationToken cancellationToken = default);

    /// <summary>Every active status, in display order - what a ticket's status picker shows.</summary>
    Task<IReadOnlyList<TicketStatus>> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>A status by its stable <see cref="TicketStatus.Slug"/>, or <c>null</c> if none matches -
    /// how ticket-lifecycle code looks up a status it depends on. See <c>TicketStatusSeeder</c>.</summary>
    Task<TicketStatus?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Whether any <see cref="Ticket"/> currently points at this status - checked before a
    /// delete so the admin UI can refuse with a clear reason instead of the database's own FK error.</summary>
    Task<bool> IsInUseAsync(Guid statusId, CancellationToken cancellationToken = default);
}
