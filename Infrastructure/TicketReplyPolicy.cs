// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Infrastructure;

/// <summary>What a customer/guest reply does to the ticket's status - shared by
/// <c>TicketsController.AddMessage</c> and <c>GuestTicketController.AddMessage</c>, the two places a
/// non-staff reply can land.</summary>
public enum TicketReplyDecision
{
    /// <summary>Refused outright - see <see cref="Ticket.PreventReopening"/>. The caller must not save
    /// the message.</summary>
    Blocked,

    /// <summary>Allowed; the ticket's status is unchanged.</summary>
    Unchanged,

    /// <summary>
    /// Allowed; <see cref="ApplyAsync"/> already moved the ticket to <c>Open</c> or <c>Reopened</c> and
    /// cleared <see cref="Ticket.ResolvedOn"/>/<see cref="Ticket.ClosedOn"/> - the caller still owns
    /// persisting that change (via <c>ITicketRepository.SaveAsync</c>) once the reply itself is saved.
    /// </summary>
    Reopened
}

/// <summary>
/// Decides what a customer/guest reply does to a <see cref="Ticket"/>'s status, and applies it.
/// </summary>
/// <remarks>
/// A <see cref="TicketStatus.IsClosed"/> status reopens to <c>Reopened</c>; a <see cref="Ticket"/>
/// merely <c>Resolved</c> (not closed - see <c>TicketStatusSeeder</c>'s remarks) reopens to <c>Open</c>
/// instead, so a ticket that was genuinely closed and came back is identifiable at a glance from one
/// that was never more than "still being worked." <see cref="Ticket.PreventReopening"/> is a per-ticket
/// flag a staff member sets deliberately (not a property of the status itself - see its own remarks for
/// why) and is checked first: it only ever matters while the ticket is closed, but when it's set it
/// beats reopening outright.
/// </remarks>
public static class TicketReplyPolicy
{
    public static async Task<TicketReplyDecision> ApplyAsync(
        Ticket ticket, ITicketStatusRepository ticketStatuses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.Status.IsClosed && ticket.PreventReopening)
        {
            return TicketReplyDecision.Blocked;
        }

        if (ticket.Status.IsClosed)
        {
            var reopened = await ticketStatuses.GetBySlugAsync(TicketStatusSeeder.Slugs.Reopened, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The Reopened ticket status is missing - TicketStatusSeeder should have created it.");

            ticket.StatusId = reopened.Id;
            ticket.Status = reopened;
            ticket.ResolvedOn = null;
            ticket.ClosedOn = null;
            return TicketReplyDecision.Reopened;
        }

        if (ticket.Status.Slug == TicketStatusSeeder.Slugs.Resolved)
        {
            var open = await ticketStatuses.GetBySlugAsync(TicketStatusSeeder.Slugs.Open, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The Open ticket status is missing - TicketStatusSeeder should have created it.");

            ticket.StatusId = open.Id;
            ticket.Status = open;
            ticket.ResolvedOn = null;
            ticket.ClosedOn = null;
            return TicketReplyDecision.Reopened;
        }

        return TicketReplyDecision.Unchanged;
    }
}
