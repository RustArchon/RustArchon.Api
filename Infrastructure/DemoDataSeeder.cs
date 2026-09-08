// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Generates a population of demonstration Organizations with a plausible two-year trading history, so
/// the reports, the receivables screens and the billing engine can be looked at with something other
/// than a handful of hand-made rows behind them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It simulates rather than fabricates.</strong> Nothing here writes an invoice, a renewal or a
/// proration by hand. It winds a <see cref="SimulatedClock"/> forward one day at a time from two years
/// ago to this morning and, on each of those days, calls the same services the running system calls -
/// <see cref="IInvoiceService"/>, <see cref="ISubscriptionService"/>, <see cref="IPaymentService"/>, and
/// <see cref="SubscriptionScheduleService"/>'s own renewal pass. The history that falls out is therefore
/// the history the system would actually have produced, which is the only kind worth testing reports
/// against: data assembled by hand agrees with whatever the person assembling it believed, and reports
/// built on it confirm that belief rather than the code.
/// </para>
/// <para>
/// A side effect worth having: seeding is a hard run at the billing engine. Two years of renewals,
/// prorated upgrades, deferred downgrades and settlement across a couple of hundred subscriptions is
/// more traffic than the engine has otherwise seen.
/// </para>
/// <para>
/// <strong>Every row it creates is marked</strong> with <see cref="MarkerId"/> in the Tenant's
/// <c>CreatedById</c>, which is what makes <c>--seed-demo=purge</c> able to remove exactly the demo
/// population and nothing else. Real accounts have a real user's id there and are never touched.
/// </para>
/// <para>
/// <strong>Development only.</strong> Reachable solely through the <c>--seed-demo</c> command-line
/// switch, itself gated on <see cref="IHostEnvironment.IsDevelopment"/> - see <c>Program.cs</c>. There is
/// no HTTP surface, so nothing about it is exposed by a running deployment.
/// </para>
/// </remarks>
public static class DemoDataSeeder
{
    /// <summary>The switch that asks for a run: <c>--seed-demo=200</c>, or <c>--seed-demo=purge</c>.</summary>
    public const string RequestKey = "seed-demo";

    /// <summary>Optional <c>--seed-demo-random=N</c>, so a run can be reproduced exactly.</summary>
    public const string RandomSeedKey = "seed-demo-random";

    /// <summary>
    /// Stamped into every generated Tenant's <c>CreatedById</c>. Recognisable on sight, impossible to
    /// collide with a real user id, and the sole thing the purge keys on.
    /// </summary>
    public static readonly Guid MarkerId = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>How far back the simulated history reaches.</summary>
    private const int HistoryMonths = 24;

    private const int DefaultCount = 200;

    /// <summary>Documentation-reserved address space (RFC 5737) - these hosts can never route anywhere.</summary>
    private const string DemoHostPrefix = "203.0.113.";

    /// <summary>True when the process was started to seed rather than to serve.</summary>
    public static bool IsRequested(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() && !string.IsNullOrWhiteSpace(configuration[RequestKey]);

    /// <summary>
    /// Runs the request in <paramref name="configuration"/> - either a purge or a generation.
    /// </summary>
    public static async Task RunAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var request = configuration[RequestKey]?.Trim() ?? string.Empty;

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();

            // Always purge first. A second run stacking a fresh population on top of an existing one
            // would double every figure on every report, which is a confusing way to discover that the
            // seeder ran twice.
            var removed = await PurgeAsync(db, logger);
            if (string.Equals(request, "purge", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Demo data purged: {Count} Organization(s) removed.", removed);
                return;
            }
        }

        if (!int.TryParse(request, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            count = DefaultCount;
        }

        if (count is < 1 or > 5000)
        {
            logger.LogError(
                "--seed-demo={Request} is not a usable population size. Give a count between 1 and 5000, "
                + "or 'purge'.", request);
            return;
        }

        var randomSeed = int.TryParse(
            configuration[RandomSeedKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 20260906;

        await GenerateAsync(services, count, randomSeed, logger);
    }

    /// <summary>
    /// Removes every Organization this seeder has ever created, and everything hanging off them.
    /// </summary>
    /// <remarks>
    /// Written as explicit ordered deletes rather than left to <c>ON DELETE CASCADE</c> from Tenant. The
    /// cascade would mostly work, but three of the edges below it are <c>RESTRICT</c>/<c>NO ACTION</c> -
    /// InvoiceLine to SubscriptionPeriod, PaymentAllocation to Invoice, CreditNote to Invoice - so
    /// whether a single delete succeeded would come down to the order Postgres happened to walk the
    /// graph in. Doing it in dependency order is a few more statements and no guesswork.
    /// </remarks>
    private static async Task<int> PurgeAsync(ApiDbContext db, ILogger logger)
    {
        var owned = $"""SELECT "Id" FROM "Tenant" WHERE "CreatedById" = '{MarkerId}'""";

        var statements = new[]
        {
            $"""DELETE FROM "PaymentAllocation" WHERE "PaymentId" IN (SELECT "Id" FROM "Payment" WHERE "TenantId" IN ({owned}))""",
            $"""DELETE FROM "InvoiceLine" WHERE "InvoiceId" IN (SELECT "Id" FROM "Invoice" WHERE "TenantId" IN ({owned}))""",
            $"""DELETE FROM "CreditNote" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "Invoice" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "Payment" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "SubscriptionPeriod" WHERE "SubscriptionId" IN (SELECT "Id" FROM "Subscription" WHERE "TenantId" IN ({owned}))""",
            $"""DELETE FROM "Subscription" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "ScheduledPlanChange" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "ConnectionLogEntry" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "RconEvent" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "PlayerKillEvent" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "PlayerSession" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "ServerInfoSnapshot" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "RustServer" WHERE "TenantId" IN ({owned})""",
            $"""DELETE FROM "Tenant" WHERE "CreatedById" = '{MarkerId}'"""
        };

        // The tenant delete is last, so its row count is the number of Organizations removed.
        var removed = 0;
        foreach (var statement in statements)
        {
            removed = await db.Database.ExecuteSqlRawAsync(statement);
        }

        // Wind the invoice counter back to just past the highest number that actually survived. Without
        // this, purging and re-seeding starts the demo series at whatever the last run reached, and the
        // first thing anyone looking at the invoice list sees is a gap - the one thing the gapless
        // numbering exists to prevent. It only ever moves the counter *down* to one past a real invoice,
        // so on a database that also holds genuine invoices it cannot hand out a number twice.
        await db.Database.ExecuteSqlRawAsync(
            $"""
            UPDATE "InvoiceNumberSequence" AS s
            SET "NextValue" = 1 + COALESCE((
                    SELECT max(substring(i."Number" from length(s."Prefix") + 1)::bigint)
                    FROM "Invoice" i
                    WHERE i."Number" IS NOT NULL
                ), 0)
            WHERE s."Scope" = '{InvoiceNumberSequence.DefaultScope}'
            """);

        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} previously-seeded demo Organization(s).", removed);
        }

        return removed;
    }

    private static async Task GenerateAsync(
        IServiceProvider services, int count, int randomSeed, ILogger logger)
    {
        var clock = services.GetRequiredService<TimeProvider>() as SimulatedClock
            ?? throw new InvalidOperationException(
                "The simulated clock is not registered. DemoDataSeeder.UseSimulatedClock must run before "
                + "the host is built - see Program.cs.");

        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var rng = new Random(randomSeed);

        var realNow = DateTimeOffset.UtcNow;
        var lastDay = DateOnly.FromDateTime(realNow.UtcDateTime);
        var firstDay = lastDay.AddMonths(-HistoryMonths);

        // The catalog, read once and kept detached. Only ever read from here (pricing a first period,
        // choosing a plan to change to); anything that writes loads its own tracked copy.
        List<Plan> catalog;
        using (var scope = scopeFactory.CreateScope())
        {
            catalog = await scope.ServiceProvider.GetRequiredService<ApiDbContext>()
                .Set<Plan>()
                .Include(p => p.Prices)
                .AsNoTracking()
                .Where(p => p.Active)
                .OrderBy(p => p.Name)
                .ToListAsync();
        }

        if (catalog.Count == 0)
        {
            logger.LogError("No active Plan rows exist - nothing to subscribe anybody to.");
            return;
        }

        var scripts = BuildScripts(count, firstDay, lastDay, catalog, rng);

        logger.LogInformation(
            "Population: {Payers} | plans {Plans} | terms {Terms}",
            string.Join(", ", scripts.GroupBy(s => s.Payer).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")),
            string.Join(", ", scripts.GroupBy(s => s.PlanName).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")),
            string.Join(", ", scripts.GroupBy(s => s.TermMonths).OrderBy(g => g.Key).Select(g => $"{g.Key}m {g.Count()}")));
        var signupsByDay = scripts
            .GroupBy(s => DateOnly.FromDateTime(s.SignupOn.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.ToList());

        var actions = new Dictionary<DateOnly, List<Func<DayContext, Task>>>();
        var byTenant = new Dictionary<Guid, TenantScript>();

        var currentDay = firstDay;
        var scheduled = 0;
        var dropped = 0;

        void Schedule(DateTimeOffset when, Func<DayContext, Task> action)
        {
            var day = DateOnly.FromDateTime(when.UtcDateTime);

            // Never into the past, and never past the end of the simulation - an action falling off the
            // end simply never happens, which is exactly what "they haven't paid yet" looks like.
            if (day < currentDay)
            {
                day = currentDay;
            }

            if (day > lastDay)
            {
                // Falls past the end of the simulation and simply never happens - which is a legitimate
                // outcome (a debt not yet chased, an invoice not yet due) but also the quietest possible
                // way for a whole behaviour to vanish from the data, so it is counted and reported.
                dropped++;
                return;
            }

            if (!actions.TryGetValue(day, out var list))
            {
                actions[day] = list = [];
            }

            scheduled++;
            list.Add(action);
        }

        var scheduler = new SubscriptionScheduleService(
            scopeFactory, clock, services.GetRequiredService<ILogger<SubscriptionScheduleService>>());

        // Invoices issued strictly before this instant have already had their settlement scheduled.
        var collectedThrough = firstDay.ToDateTimeOffset(TimeSpan.Zero);

        logger.LogInformation(
            "Seeding {Count} demo Organizations across {Months} months of simulated history "
            + "({From:d MMM yyyy} - {To:d MMM yyyy}), random seed {Seed}.",
            count, HistoryMonths, firstDay, lastDay, randomSeed);

        var progressAt = DateTime.UtcNow;

        for (currentDay = firstDay; currentDay <= lastDay; currentDay = currentDay.AddDays(1))
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            var context = new DayContext(
                db,
                scope.ServiceProvider.GetRequiredService<ISubscriptionService>(),
                scope.ServiceProvider.GetRequiredService<IInvoiceService>(),
                scope.ServiceProvider.GetRequiredService<IPaymentService>(),
                clock,
                logger,
                Schedule,
                rng,
                lastDay.ToDateTimeOffset(TimeSpan.Zero));

            // 1. Yesterday's invoices - whoever raised them - get their settlement planned. Done at the
            //    start of a day rather than after each issuing step so there is one collection point
            //    instead of three, and no chance of an invoice slipping between them.
            clock.Now = At(currentDay, 6);
            await CollectNewInvoicesAsync(context, byTenant, collectedThrough, clock.Now);
            collectedThrough = clock.Now;

            // 2. The real renewal engine, unmodified: periods that ended overnight roll forward and
            //    scheduled downgrades that have come due land, both raising their own invoices.
            clock.Now = At(currentDay, 8);
            await scheduler.RunPassAsync(CancellationToken.None);

            // 3. Today's sign-ups.
            if (signupsByDay.TryGetValue(currentDay, out var joining))
            {
                foreach (var script in joining)
                {
                    clock.Now = At(currentDay, rng.Next(9, 22));
                    await CreateOrganizationAsync(context, script, catalog);
                    byTenant[script.Id] = script;
                }
            }

            // 4. Everything else that was booked for today: payments, plan changes, servers coming
            //    online, refunds, credits, write-offs, cancellations. Indexed rather than foreach'd
            //    because an action may book another for the same day (a payment that is refunded, a
            //    credit note followed by settlement of the remainder).
            clock.Now = At(currentDay, 16);
            if (actions.TryGetValue(currentDay, out var due))
            {
                for (var i = 0; i < due.Count; i++)
                {
                    try
                    {
                        await due[i](context);
                    }
                    catch (Exception ex)
                    {
                        // One tenant's odd corner should not cost the other 199 their history.
                        logger.LogWarning(ex, "A simulated action on {Day} failed; continuing.", currentDay);
                    }
                }

                actions.Remove(currentDay);
            }

            if ((DateTime.UtcNow - progressAt).TotalSeconds >= 10)
            {
                progressAt = DateTime.UtcNow;
                logger.LogInformation("  ... simulated through {Day:d MMM yyyy}.", currentDay);
            }
        }

        clock.Now = realNow;
        logger.LogInformation(
            "Simulation finished: {Scheduled} actions ran, {Dropped} fell past the end of history.",
            scheduled, dropped);
        await ReportAsync(scopeFactory, logger);
    }

    /// <summary>
    /// Creates one Organization the way <c>AccountBootstrapController.EnsureTenant</c> does - tenant,
    /// open subscription, first billing period, and an invoice for it, since service is paid in advance.
    /// </summary>
    /// <remarks>
    /// The identity half of sign-up (the user, the Owner role, the membership) is deliberately not
    /// reproduced: those live in the Panel's own Identity store, not this database, and nothing in the
    /// billing or reporting band reads them. A demo Organization is one nobody can log in to.
    /// </remarks>
    private static async Task CreateOrganizationAsync(DayContext ctx, TenantScript script, List<Plan> catalog)
    {
        var now = ctx.Clock.Now;
        var plan = catalog.First(p => p.Name == script.PlanName);

        var tenant = new Tenant
        {
            Name = script.Name,
            ContactEmail = script.ContactEmail,
            IsActive = true,
            CreatedById = MarkerId,
            CreatedOn = now
        };
        ctx.Db.Set<Tenant>().Add(tenant);
        await ctx.Db.SaveChangesAsync();

        script.Id = tenant.Id;

        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            PlanId = plan.Id,
            StartDate = now,
            Status = SubscriptionStatus.Active
        };
        ctx.Db.Set<Subscription>().Add(subscription);
        await ctx.Db.SaveChangesAsync();

        var quantity = PlanChangeCalculator.ResolveQuantity(plan, script.TermMonths, requested: null, serverCount: 0);
        var periodEnd = now.AddMonths(script.TermMonths);

        var period = new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            TermMonths = script.TermMonths,
            Quantity = quantity,
            PeriodStart = now,
            PeriodEnd = periodEnd,
            StartDate = now,
            EndDate = periodEnd,
            EarnedAmount = PlanChangeCalculator.PriceFor(plan, script.TermMonths, quantity)
        };
        ctx.Db.Set<SubscriptionPeriod>().Add(period);
        await ctx.Db.SaveChangesAsync();

        await ctx.Invoices.IssueForPeriodAsync(
            period,
            period.EarnedAmount,
            $"{plan.Name} - {BillingTerms.Describe(script.TermMonths)}, "
            + $"{now:d MMM yyyy} - {periodEnd:d MMM yyyy}");

        // Servers come online a few days after sign-up, which is what makes the "days to activate"
        // figure on the new-signups report something other than zero for everybody.
        if (script.ServerCount > 0)
        {
            ctx.Schedule(now.AddDays(ctx.Rng.Next(0, 12)), c => AddServersAsync(c, script));
        }

        if (script.Change is { } change)
        {
            ctx.Schedule(change.On, c => ChangePlanAsync(c, script, change));
        }

        if (script.ChurnOn is { } churnOn)
        {
            ctx.Schedule(churnOn, c => CancelAsync(c, script));
        }
    }

    /// <summary>
    /// Brings this Organization's servers online.
    /// </summary>
    /// <remarks>
    /// <strong>Created disabled, on documentation-reserved addresses.</strong> An enabled RustServer row
    /// is an instruction to the Worker to open an RCON connection to it, and a few hundred of those
    /// pointed at hosts that do not exist would have the Worker doing nothing but failing to connect.
    /// The rows are what the reports and the entitlement checks count; the connections are not.
    /// </remarks>
    private static async Task AddServersAsync(DayContext ctx, TenantScript script)
    {
        var now = ctx.Clock.Now;

        // Distinct flavours rather than a roll per server: RustServer carries a unique index on
        // (TenantId, Name), so two rolls landing on the same one is a failed insert, not a duplicate name.
        var flavours = ServerFlavours.OrderBy(_ => ctx.Rng.Next()).Take(script.ServerCount).ToList();

        for (var i = 0; i < script.ServerCount; i++)
        {
            ctx.Db.Set<RustServer>().Add(new RustServer
            {
                TenantId = script.Id,
                Name = $"{script.ShortName} {flavours[i]}",
                Host = DemoHostPrefix + ctx.Rng.Next(2, 250).ToString(CultureInfo.InvariantCulture),
                Port = 28016 + (i * 10),
                RconPassword = "demo-data-not-a-real-password",
                Description = "Seeded demo server.",
                IsEnabled = false,
                ConnectionStatus = RconConnectionStatus.Disconnected,
                ConnectionStatusDetail = "Demo data - this host does not exist.",
                ConnectionStatusChangedAtUtc = now,
                CreatedById = MarkerId,
                CreatedOn = now
            });
        }

        await ctx.Db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts a plan change through the real <see cref="ISubscriptionService"/>, which decides on its own
    /// whether it applies now with proration or waits for the end of the period.
    /// </summary>
    private static async Task ChangePlanAsync(
        DayContext ctx, TenantScript script, (DateTimeOffset On, string PlanName, int TermMonths) change)
    {
        if (script.Churned)
        {
            return;
        }

        var target = await ctx.Db.Set<Plan>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == change.PlanName && p.Active);

        if (target is null)
        {
            return;
        }

        var quote = await ctx.Subscriptions.ApplyAsync(script.Id, target.Id, change.TermMonths);

        if (!quote.Allowed)
        {
            ctx.Logger.LogDebug(
                "Simulated plan change for {Tenant} to {Plan} was declined: {Reason}",
                script.Name, change.PlanName, quote.BlockedReason);
        }
    }

    /// <summary>
    /// Ends an Organization: the open subscription closes, any queued change is cancelled, and the
    /// tenant is soft-deleted.
    /// </summary>
    /// <remarks>
    /// The soft delete is what makes the cancellation stick. <see cref="SubscriptionBackfiller"/> gives a
    /// plan to every tenant that has no open subscription, on the reasoning that a planless tenant is a
    /// bug rather than an intent - and it is right, but it would also hand a fresh subscription to
    /// everyone who ever left. A soft-deleted tenant is invisible to it (and to the reports) through
    /// JumpStart's global soft-delete filter, so "deliberately ended" stays ended without needing the
    /// explicit marker that class's remarks call for.
    /// </remarks>
    private static async Task CancelAsync(DayContext ctx, TenantScript script)
    {
        var now = ctx.Clock.Now;

        var open = await ctx.Db.Set<Subscription>()
            .FirstOrDefaultAsync(s => s.TenantId == script.Id && s.EndDate == null);

        if (open is not null)
        {
            open.EndDate = now;
            open.Status = SubscriptionStatus.Cancelled;
        }

        var queued = await ctx.Db.Set<ScheduledPlanChange>()
            .Where(c => c.TenantId == script.Id && c.AppliedOn == null && c.CancelledOn == null)
            .ToListAsync();

        foreach (var change in queued)
        {
            change.CancelledOn = now;
        }

        var tenant = await ctx.Db.Set<Tenant>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == script.Id);

        if (tenant is not null)
        {
            tenant.IsActive = false;
            tenant.DeletedOn = now;
            tenant.DeletedById = MarkerId;
        }

        await ctx.Db.SaveChangesAsync();
        script.Churned = true;
    }

    /// <summary>
    /// Finds invoices raised since the last sweep and books whatever their Organization's payment
    /// behaviour says happens to them.
    /// </summary>
    private static async Task CollectNewInvoicesAsync(
        DayContext ctx, Dictionary<Guid, TenantScript> byTenant, DateTimeOffset from, DateTimeOffset to)
    {
        var raised = await ctx.Db.Set<Invoice>()
            .Where(i => i.IssuedOn >= from && i.IssuedOn < to && i.Status == InvoiceStatus.Open)
            .Select(i => new { i.Id, i.TenantId, i.IssuedOn })
            .ToListAsync();

        foreach (var invoice in raised)
        {
            if (!byTenant.TryGetValue(invoice.TenantId, out var script) || script.Churned)
            {
                continue;
            }

            var issued = invoice.IssuedOn!.Value;
            var ordinal = ++script.InvoicesRaised;

            switch (script.Payer)
            {
                case Payer.Prompt:
                    Pay(issued.AddDays(ctx.Rng.Next(0, 5)));
                    break;

                case Payer.Slow:
                    // Past the 14-day terms often enough to sit in the current-and-overdue bucket.
                    Pay(issued.AddDays(ctx.Rng.Next(9, 39)));
                    break;

                case Payer.Delinquent:
                    // Paid like anybody else until the card stopped working. Everything after that is
                    // what fills the aging buckets.
                    if (issued < script.StopPayingOn)
                    {
                        Pay(issued.AddDays(ctx.Rng.Next(0, 6)));
                    }

                    break;

                case Payer.Partial:
                    if (issued < script.PartialFrom)
                    {
                        Pay(issued.AddDays(ctx.Rng.Next(0, 5)));
                    }
                    else
                    {
                        Pay(issued.AddDays(ctx.Rng.Next(2, 9)), fraction: 0.35m + (decimal)(ctx.Rng.NextDouble() * 0.4));
                    }

                    break;

                case Payer.Overpayer:
                    // Pays in round numbers, so most months leave a little sitting unallocated against
                    // the account - money received and not yet applied, which is a state receivables has
                    // to be able to show.
                    Pay(issued.AddDays(ctx.Rng.Next(0, 4)), roundUpTo: 25m);
                    break;

                case Payer.Refunded:
                {
                    var after = ctx.Rng.Next(20, 60);
                    Pay(
                        issued.AddDays(ctx.Rng.Next(0, 4)),
                        reverseAfterDays: TakeEvent(after + 5) ? after : null);
                    break;
                }

                case Payer.Credited:
                    if (TakeEvent(3))
                    {
                        ctx.Schedule(issued.AddDays(3), c => CreditAsync(c, script, invoice.Id));
                    }

                    Pay(issued.AddDays(ctx.Rng.Next(4, 9)));
                    break;

                case Payer.WrittenOff:
                {
                    // One debt that was genuinely owed and never collected; everything since is current.
                    var chased = ctx.Rng.Next(45, 75);
                    if (TakeEvent(chased))
                    {
                        ctx.Schedule(issued.AddDays(chased), c => WriteOffAsync(c, script, invoice.Id));
                    }
                    else
                    {
                        Pay(issued.AddDays(ctx.Rng.Next(0, 5)));
                    }

                    break;
                }

                case Payer.Voided:
                    // Raised in error and withdrawn before anything was paid against it.
                    if (TakeEvent(2))
                    {
                        ctx.Schedule(issued.AddDays(2), c => VoidAsync(c, script, invoice.Id));
                    }
                    else
                    {
                        Pay(issued.AddDays(ctx.Rng.Next(0, 5)));
                    }

                    break;
            }

            continue;

            void Pay(DateTimeOffset on, decimal? fraction = null, decimal? roundUpTo = null, int? reverseAfterDays = null) =>
                ctx.Schedule(on, c => PayAsync(c, script, invoice.Id, fraction, roundUpTo, reverseAfterDays));

            // Whether this is the invoice the Organization's one-off event lands on. Two conditions, and
            // the second is the one that was learned the hard way: the invoice has to be old enough that
            // the follow-up - a write-off chased for two months, a chargeback six weeks later - still
            // falls inside the simulated history. Without it, an event booked against a recent invoice is
            // silently dropped at the end of the run and the behaviour leaves no trace at all.
            bool TakeEvent(int followUpDays)
            {
                if (script.EventFired
                    || ordinal < script.EventOrdinal
                    || issued.AddDays(followUpDays) > ctx.Horizon)
                {
                    return false;
                }

                script.EventFired = true;
                return true;
            }
        }
    }

    private static async Task PayAsync(
        DayContext ctx, TenantScript script, Guid invoiceId,
        decimal? fraction, decimal? roundUpTo, int? reverseAfterDays)
    {
        if (script.Churned)
        {
            return;
        }

        var invoice = await ctx.Db.Set<Invoice>().FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice is null || invoice.Status != InvoiceStatus.Open)
        {
            return;
        }

        var outstanding = invoice.AmountOutstanding;
        if (outstanding <= 0m)
        {
            return;
        }

        var amount = fraction is { } part
            ? Math.Round(outstanding * part, 2, MidpointRounding.AwayFromZero)
            : roundUpTo is { } step
                ? Math.Ceiling(outstanding / step) * step
                : outstanding;

        if (amount <= 0m)
        {
            return;
        }

        var roll = ctx.Rng.Next(100);
        var method = roll < 72 ? PaymentMethod.Card : roll < 92 ? PaymentMethod.BankTransfer : PaymentMethod.Manual;

        var payment = await ctx.Payments.RecordPaymentAsync(
            invoiceId, amount, method, ctx.Clock.Now, ReferenceFor(method, ctx.Rng));

        if (reverseAfterDays is { } days)
        {
            var status = ctx.Rng.Next(2) == 0 ? PaymentStatus.Refunded : PaymentStatus.Disputed;
            ctx.Schedule(
                ctx.Clock.Now.AddDays(days),
                c => c.Payments.ReversePaymentAsync(payment.Id, status));
        }
    }

    private static async Task CreditAsync(DayContext ctx, TenantScript script, Guid invoiceId)
    {
        if (script.Churned)
        {
            return;
        }

        var invoice = await ctx.Db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice is null || invoice.AmountOutstanding <= 0m)
        {
            return;
        }

        var amount = Math.Min(invoice.AmountOutstanding, Math.Round(invoice.Total * 0.3m, 2, MidpointRounding.AwayFromZero));
        if (amount <= 0m)
        {
            return;
        }

        await ctx.Payments.IssueCreditNoteAsync(
            invoiceId, amount, CreditReasons[ctx.Rng.Next(CreditReasons.Length)]);
    }

    private static async Task WriteOffAsync(DayContext ctx, TenantScript script, Guid invoiceId)
    {
        if (script.Churned)
        {
            return;
        }

        await ctx.Payments.WriteOffInvoiceAsync(
            invoiceId, "Chased three times, no response. Written off as bad debt.");
    }

    private static async Task VoidAsync(DayContext ctx, TenantScript script, Guid invoiceId)
    {
        if (script.Churned)
        {
            return;
        }

        await ctx.Payments.VoidInvoiceAsync(invoiceId, "Raised in error - duplicate of the period already billed.");
    }

    /// <summary>Summarises what the run produced, so a look at the log says whether it worked.</summary>
    private static async Task ReportAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();

        var tenants = await db.Set<Tenant>().IgnoreQueryFilters()
            .CountAsync(t => t.CreatedById == MarkerId);
        var live = await db.Set<Tenant>().IgnoreQueryFilters()
            .CountAsync(t => t.CreatedById == MarkerId && t.DeletedOn == null);
        var demoIds = db.Set<Tenant>().IgnoreQueryFilters()
            .Where(t => t.CreatedById == MarkerId)
            .Select(t => t.Id);

        var invoices = await db.Set<Invoice>().Where(i => demoIds.Contains(i.TenantId)).ToListAsync();
        var payments = await db.Set<Payment>().CountAsync(p => demoIds.Contains(p.TenantId));
        var servers = await db.Set<RustServer>().IgnoreQueryFilters()
            .CountAsync(s => demoIds.Contains(s.TenantId));
        var periods = await db.Set<SubscriptionPeriod>()
            .CountAsync(p => demoIds.Contains(p.Subscription.TenantId));
        var pending = await db.Set<ScheduledPlanChange>()
            .CountAsync(c => c.AppliedOn == null && c.CancelledOn == null && demoIds.Contains(c.TenantId));

        var billed = invoices.Where(i => i.Status != InvoiceStatus.Void).Sum(i => i.Total);
        var outstanding = invoices
            .Where(i => i.Status == InvoiceStatus.Open)
            .Sum(i => i.AmountOutstanding);

        logger.LogInformation(
            """
            Demo data seeded.
              Organizations      {Tenants} ({Live} current, {Churned} cancelled)
              Servers            {Servers}
              Billing periods    {Periods}
              Invoices           {Invoices} - {Open} open, {Paid} paid, {Void} void, {Bad} uncollectible
              Payments           {Payments}
              Pending changes    {Pending}
              Billed             {Billed} USD total, {Outstanding} USD still outstanding
            """,
            tenants, live, tenants - live,
            servers,
            periods,
            invoices.Count,
            invoices.Count(i => i.Status == InvoiceStatus.Open),
            invoices.Count(i => i.Status == InvoiceStatus.Paid),
            invoices.Count(i => i.Status == InvoiceStatus.Void),
            invoices.Count(i => i.Status == InvoiceStatus.Uncollectible),
            payments,
            pending,
            billed,
            outstanding);
    }

    /// <summary>
    /// Decides, up front, who every Organization is going to be - when they joined, on what, how they
    /// pay, and what happens to them.
    /// </summary>
    /// <remarks>
    /// Written out in one pass before the simulation starts so the population is reproducible from the
    /// random seed alone, and so the distribution is a thing that can be read in one place rather than
    /// inferred from scattered dice rolls.
    /// </remarks>
    private static List<TenantScript> BuildScripts(
        int count, DateOnly firstDay, DateOnly lastDay, List<Plan> catalog, Random rng)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scripts = new List<TenantScript>(count);
        var span = (lastDay.ToDateTimeOffset(TimeSpan.Zero) - firstDay.ToDateTimeOffset(TimeSpan.Zero)).TotalDays - 2;

        // Plans, weighted the way a freemium tier usually lands: a broad free base, most of the paying
        // customers in the middle, a thin top.
        var planWeights = new (string Name, int Weight)[]
        {
            ("Wood", 22), ("Stone", 34), ("Metal", 28), ("HQM", 16)
        };

        var payers = Deal(PayerWeights, count, rng);
        var plans = Deal(planWeights, count, rng);
        var terms = Deal(TermWeights, count, rng);

        for (var i = 0; i < count; i++)
        {
            string name;
            do
            {
                name = $"{Adjectives[rng.Next(Adjectives.Length)]} {Nouns[rng.Next(Nouns.Length)]}"
                    + Suffixes[rng.Next(Suffixes.Length)];
            }
            while (!names.Add(name));

            // Sign-ups skewed toward the recent end: a growing platform has more customers this quarter
            // than it did two years ago, and a flat distribution makes every cohort chart a straight line.
            var signup = firstDay.ToDateTimeOffset(TimeSpan.Zero)
                .AddDays(span * Math.Pow(rng.NextDouble(), 0.55))
                .AddHours(rng.Next(6, 22));

            var planName = plans[i];
            var plan = catalog.FirstOrDefault(p => p.Name == planName) ?? catalog[0];

            var term = terms[i];

            var ceiling = Math.Min(plan.MaximumServers ?? 3, 6);
            var servers = rng.Next(100) switch
            {
                < 8 => 0,                                   // signed up, never set anything up
                < 70 => Math.Min(1, ceiling),
                _ => rng.Next(1, Math.Max(2, ceiling + 1))
            };

            var script = new TenantScript
            {
                Name = name,
                ContactEmail = $"owner@{Slug(name)}.example",
                SignupOn = signup,
                PlanName = planName,
                TermMonths = term,
                ServerCount = servers,
                Payer = payers[i]
            };

            var lastInstant = lastDay.ToDateTimeOffset(TimeSpan.Zero);

            // Which of this Organization's invoices the one-off event lands on - the credit note, the bad
            // debt, the invoice raised in error. Pinning it to "the second one" is what an earlier version
            // did, and it meant an account billed annually, or one that signed up in the spring, never
            // reached it: those behaviours had a weight in the mix and produced nothing in the data. The
            // second invoice where there is one, otherwise the only one they will ever get.
            var expectedInvoices = (int)((lastInstant - script.SignupOn).TotalDays / (30.44 * term));
            script.EventOrdinal = Math.Clamp(expectedInvoices, 1, 2);

            if (script.Payer == Payer.Delinquent)
            {
                // Stopped paying somewhere between one and five months ago, which spreads the demo
                // population across every aging bucket instead of piling it into one.
                script.StopPayingOn = lastInstant.AddDays(-rng.Next(35, 160));
            }

            if (script.Payer == Payer.Partial)
            {
                script.PartialFrom = lastInstant.AddDays(-rng.Next(30, 150));
            }

            // A plan change for roughly a quarter of them, somewhere in the middle of their life.
            if (rng.Next(100) < 26)
            {
                var lifetime = (lastInstant - signup).TotalDays;
                if (lifetime > 45)
                {
                    var on = signup.AddDays(rng.Next(30, (int)lifetime - 5));
                    var upgrade = rng.Next(100) < 62;
                    var order = new[] { "Wood", "Stone", "Metal", "HQM" };
                    var at = Array.IndexOf(order, planName);
                    var toIndex = upgrade ? Math.Min(at + 1, order.Length - 1) : Math.Max(at - 1, 0);

                    // A term change on its own is a real change too - and the only one available to
                    // somebody already at the top or bottom of the ladder.
                    var toTerm = toIndex == at || rng.Next(100) < 30
                        ? (term == BillingTerms.Monthly ? BillingTerms.Annual : BillingTerms.Monthly)
                        : term;

                    if (toIndex != at || toTerm != term)
                    {
                        script.Change = (on, order[toIndex], toTerm);
                    }
                }
            }

            // A handful cancel outright.
            if (rng.Next(100) < 9)
            {
                var lifetime = (lastInstant - signup).TotalDays;
                if (lifetime > 70)
                {
                    script.ChurnOn = signup.AddDays(rng.Next(60, (int)lifetime - 2));
                }
            }

            scripts.Add(script);
        }

        // A few requests made recently enough to still be sitting in the queue at the end of the run -
        // the scheduled-changes report needs something to show. A downgrade defers to the end of the
        // period by definition, so asking for one in the last fortnight leaves it pending.
        foreach (var script in scripts
            .Where(s => s.ChurnOn is null && s.Change is null && s.PlanName != "Wood")
            .OrderBy(_ => rng.Next())
            .Take(Math.Max(3, count / 16)))
        {
            var order = new[] { "Wood", "Stone", "Metal", "HQM" };
            var at = Array.IndexOf(order, script.PlanName);
            var on = lastDay.ToDateTimeOffset(TimeSpan.Zero).AddDays(-rng.Next(2, 16));

            if (on > script.SignupOn.AddDays(20))
            {
                script.Change = (on, order[Math.Max(at - 1, 0)], script.TermMonths);
            }
        }

        return scripts.OrderBy(s => s.SignupOn).ToList();
    }

    /// <summary>
    /// Deals <paramref name="count"/> values out in the given proportions, shuffled - rather than
    /// rolling independently for each one.
    /// </summary>
    /// <remarks>
    /// Independent rolls are the obvious way to do this and the wrong one for a fixture. A behaviour
    /// weighted at 5% of 220 accounts should land on eleven of them; sampled independently it landed on
    /// five, and since two of those five had no room left in the timeline for the follow-up action, a
    /// whole branch of the billing model - bad debt - ended up represented by a single row. Dealing from
    /// a deck makes the proportions the ones written down, which is the point of a fixture: the mix is a
    /// decision, not an outcome.
    /// </remarks>
    private static List<T> Deal<T>((T Value, int Weight)[] options, int count, Random rng)
    {
        var total = options.Sum(o => o.Weight);
        var deck = new List<T>(count);

        foreach (var option in options)
        {
            var share = (int)Math.Round(count * (double)option.Weight / total, MidpointRounding.AwayFromZero);
            for (var i = 0; i < share && deck.Count < count; i++)
            {
                deck.Add(option.Value);
            }
        }

        // Rounding can leave the deck a card or two short; the first option is the commonest, so it is
        // the one to pad with.
        while (deck.Count < count)
        {
            deck.Add(options[0].Value);
        }

        return deck.OrderBy(_ => rng.Next()).ToList();
    }

    private static DateTimeOffset At(DateOnly day, int hour) =>
        new(day.Year, day.Month, day.Day, hour, 0, 0, TimeSpan.Zero);

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-')
            .Replace("--", "-", StringComparison.Ordinal);

    private static string ReferenceFor(PaymentMethod method, Random rng) => method switch
    {
        PaymentMethod.Card => "ch_" + rng.Next(100000, 999999).ToString(CultureInfo.InvariantCulture),
        PaymentMethod.BankTransfer => "FT" + rng.Next(10000000, 99999999).ToString(CultureInfo.InvariantCulture),
        _ => "Cheque " + rng.Next(1000, 9999).ToString(CultureInfo.InvariantCulture)
    };

    private static readonly (int Value, int Weight)[] TermWeights =
    [
        (BillingTerms.Monthly, 68),
        (BillingTerms.Quarterly, 18),
        (BillingTerms.Annual, 14)
    ];

    /// <remarks>
    /// Weighted for <em>coverage</em> rather than for realism. A real book of business is far more
    /// prompt-paying than this; a demo population that reflected that faithfully would leave the void,
    /// write-off and chargeback paths with nothing in them to look at, which is the opposite of the
    /// point. The shape of each behaviour is honest even though the mix is not.
    /// </remarks>
    private static readonly (Payer Value, int Weight)[] PayerWeights =
    [
        (Payer.Prompt, 44),
        (Payer.Slow, 14),
        (Payer.Delinquent, 9),
        (Payer.Partial, 7),
        (Payer.Overpayer, 5),
        (Payer.Refunded, 6),
        (Payer.Credited, 6),
        (Payer.WrittenOff, 5),
        (Payer.Voided, 4)
    ];

    private static readonly string[] CreditReasons =
    [
        "Goodwill - two hours of RCON downtime on the 14th.",
        "Billed for a slot they released before the period started.",
        "Support agreed a partial refund for the failed migration.",
        "Compensation for the console history lost in the retention bug."
    ];

    private static readonly string[] ServerFlavours =
    [
        "Main", "2x Vanilla", "5x Solo/Duo", "Modded PvE", "Hardcore", "Build Server",
        "Trio", "Weekly Wipe", "Zerg Wars", "Low Pop"
    ];

    private static readonly string[] Adjectives =
    [
        "Iron", "Rusted", "Scrap", "Salvage", "Radiated", "Frozen", "Blackout", "Nomad", "Bandit",
        "Crimson", "Hollow", "Savage", "Wasteland", "Feral", "Toxic", "Barren", "Vagrant", "Grizzled",
        "Steel", "Concrete", "Sulfur", "Cobalt", "Ashen", "Rogue", "Twisted", "Silent", "Broken",
        "Midnight", "Northern", "Scorched"
    ];

    private static readonly string[] Nouns =
    [
        "Vultures", "Raiders", "Syndicate", "Collective", "Coalition", "Legion", "Brigade", "Outfit",
        "Compound", "Bunker", "Foundry", "Refinery", "Quarry", "Junction", "Militia", "Cartel",
        "Frontier", "Wolves", "Ravens", "Hyenas", "Jackals", "Bastion", "Depot", "Works", "Union",
        "Company"
    ];

    private static readonly string[] Suffixes =
    [
        "", "", "", " Gaming", " Network", " Servers", " Clan", " Community", " EU", " NA"
    ];

    /// <summary>How an Organization behaves when an invoice lands.</summary>
    private enum Payer
    {
        Prompt,
        Slow,
        Delinquent,
        Partial,
        Overpayer,
        Refunded,
        Credited,
        WrittenOff,
        Voided
    }

    /// <summary>What one simulated Organization is going to do, decided before the run starts.</summary>
    private sealed class TenantScript
    {
        public Guid Id { get; set; }
        public required string Name { get; init; }
        public required string ContactEmail { get; init; }
        public required DateTimeOffset SignupOn { get; init; }
        public required string PlanName { get; init; }
        public required int TermMonths { get; init; }
        public required int ServerCount { get; init; }
        public required Payer Payer { get; init; }

        public DateTimeOffset? StopPayingOn { get; set; }
        public DateTimeOffset? PartialFrom { get; set; }
        public DateTimeOffset? ChurnOn { get; set; }
        public (DateTimeOffset On, string PlanName, int TermMonths)? Change { get; set; }

        /// <summary>How many invoices this Organization has been sent so far.</summary>
        public int InvoicesRaised { get; set; }

        /// <summary>Which of them the one-off event lands on - see where it is chosen, in BuildScripts.
        /// Comparing against this is what keeps one credit note or one bad debt to an account rather than
        /// one per month.</summary>
        public int EventOrdinal { get; set; } = 2;

        /// <summary>Set once the one-off event has been booked, so it happens exactly once.</summary>
        public bool EventFired { get; set; }

        public bool Churned { get; set; }

        /// <summary>The name without its suffix, for naming servers.</summary>
        public string ShortName => Name.Split(' ') is [var first, var second, ..] ? $"{first} {second}" : Name;
    }

    /// <summary>Everything one simulated day needs, resolved from that day's own DI scope.</summary>
    private sealed record DayContext(
        ApiDbContext Db,
        ISubscriptionService Subscriptions,
        IInvoiceService Invoices,
        IPaymentService Payments,
        SimulatedClock Clock,
        ILogger Logger,
        Action<DateTimeOffset, Func<DayContext, Task>> Schedule,
        Random Rng,
        DateTimeOffset Horizon);
}

/// <summary>Small conveniences the seeder needs for moving between <see cref="DateOnly"/> and instants.</summary>
internal static class DemoDateExtensions
{
    public static DateTimeOffset ToDateTimeOffset(this DateOnly day, TimeSpan offset) =>
        new(day.Year, day.Month, day.Day, 0, 0, 0, offset);
}
