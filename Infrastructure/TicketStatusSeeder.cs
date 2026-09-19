// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Seeds the starter <see cref="TicketStatus"/> rows on a fresh deployment, all seven
/// <see cref="TicketStatus.IsProtected"/> - the six the ticket lifecycle itself looks up by slug
/// (<see cref="Slugs.Submitted"/>, <see cref="Slugs.Open"/>, <see cref="Slugs.WaitingOnCustomer"/>,
/// <see cref="Slugs.Resolved"/>, <see cref="Slugs.Closed"/>, <see cref="Slugs.Reopened"/>) plus
/// <see cref="Slugs.Cancelled"/>, which no code assigns automatically but is permanent by design - see
/// its own remarks - matching a self-hoster's likely workflow out of the box while leaving them free to
/// add, rename, or reorder anything beyond these seven.
/// </summary>
/// <remarks>
/// <strong>Idempotent</strong>, safe to call on every Api startup (mirrors <see cref="QueueSeeder"/>) -
/// gated on "does any TicketStatus exist at all," so an admin's later edits are never overwritten by a
/// later restart re-running this.
/// </remarks>
public static class TicketStatusSeeder
{
    /// <summary>The stable, code-referenceable keys - see <see cref="Data.TicketStatus.Slug"/>.</summary>
    public static class Slugs
    {
        /// <summary>Just created, not yet worked - the status a fresh <see cref="Ticket"/> starts in.</summary>
        public const string Submitted = "submitted";

        /// <summary>A staff member has picked it up and is actively working it. Also where a
        /// <see cref="Ticket"/> returns to when a submitter replies to a <see cref="Resolved"/> one -
        /// see <see cref="Reopened"/> for the equivalent from an <c>IsClosed</c> status.</summary>
        public const string Open = "open";

        /// <summary>Staff replied and is waiting on the submitter's next message.</summary>
        public const string WaitingOnCustomer = "waiting-on-customer";

        /// <summary>Staff considers this resolved - see <see cref="Ticket.ResolvedOn"/>. Not
        /// <see cref="TicketStatus.IsClosed"/> by default - a resolved ticket still reopens on reply.</summary>
        public const string Resolved = "resolved";

        /// <summary>Closed out - see <see cref="Ticket.ClosedOn"/>.</summary>
        public const string Closed = "closed";

        /// <summary>
        /// Where a <see cref="Ticket"/> lands when a customer/guest replies to one whose current status
        /// is <see cref="TicketStatus.IsClosed"/> - distinct from <see cref="Open"/> (which a
        /// <see cref="Resolved"/> reply reopens to) so a closed-then-revived ticket is identifiable at a
        /// glance, and queryable, without relying on its name. See <c>TicketReplyPolicy</c>.
        /// </summary>
        public const string Reopened = "reopened";

        /// <summary>
        /// Withdrawn rather than worked to completion. No code ever assigns this automatically - a
        /// staff member always chooses it deliberately from the status picker - but it's still
        /// <see cref="TicketStatus.IsProtected"/> (a permanent, undeletable status by design), unlike
        /// a custom status an admin adds later.
        /// </summary>
        public const string Cancelled = "cancelled";
    }

    public static async Task EnsureDefaultsAsync(ApiDbContext dbContext, ILogger logger)
    {
        if (await dbContext.Set<TicketStatus>().AnyAsync())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        dbContext.Set<TicketStatus>().AddRange(
            Build(Slugs.Submitted, "Submitted", isClosed: false, isProtected: true, order: 10, now),
            Build(Slugs.Open, "Open", isClosed: false, isProtected: true, order: 20, now),
            Build(Slugs.Reopened, "Reopened", isClosed: false, isProtected: true, order: 25, now),
            Build(Slugs.WaitingOnCustomer, "Waiting on Customer", isClosed: false, isProtected: true, order: 30, now),
            Build(Slugs.Resolved, "Resolved", isClosed: false, isProtected: true, order: 40, now),
            Build(Slugs.Closed, "Closed", isClosed: true, isProtected: true, order: 50, now),
            Build(Slugs.Cancelled, "Cancelled", isClosed: true, isProtected: true, order: 60, now));

        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Seeded the seven starter TicketStatus rows (Submitted, Open, Reopened, Waiting on Customer, "
            + "Resolved, Closed, Cancelled), all protected.");
    }

    private static TicketStatus Build(
        string slug, string name, bool isClosed, bool isProtected, int order, DateTimeOffset now) => new()
    {
        Slug = slug,
        Name = name,
        IsClosed = isClosed,
        IsProtected = isProtected,
        IsActive = true,
        DisplayOrder = order,
        CreatedById = Guid.Empty,
        CreatedOn = now
    };
}
