// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <summary>
/// Chases unpaid invoices: a friendly reminder before one falls due, a past-due notice with a
/// suspension countdown once it hasn't been paid, and the suspension itself if that countdown runs out
/// still unpaid.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three stages, each idempotent for a different reason.</strong> The due-soon reminder is a
/// one-time nudge with no state transition behind it, so it needs its own marker
/// (<see cref="Invoice.DueSoonReminderSentOn"/>) to avoid re-sending on every pass for the whole
/// reminder window. The past-due and suspension stages are <see cref="Subscription.Status"/>
/// transitions, and <see cref="IOrganizationLifecycleService.SetStatusAsync"/> is already a no-op once
/// the subscription is already at the requested status - so calling it every pass for a tenant that's
/// already <see cref="SubscriptionStatus.PastDue"/> or already <see cref="SubscriptionStatus.Suspended"/>
/// costs a query and nothing else.
/// </para>
/// <para>
/// <strong>A separate service from <see cref="SubscriptionScheduleService"/>, not a third pass bolted
/// onto it.</strong> That service's job is keeping a subscription's own dates and plan correct -
/// renewals and scheduled plan changes - and knows nothing about money actually arriving. This one's
/// job is entirely about payment: whether an issued invoice gets paid, and what happens to service if
/// it doesn't. They happen to run on the same cadence for the same reason (an hourly sweep is cheap and
/// nothing here is latency-sensitive), not because they're one concern - which is also why neither
/// queries <see cref="Invoice"/>/<see cref="Subscription"/> through the other's repository.
/// </para>
/// <para>
/// <strong>Reactivation is deliberately not this service's job.</strong> Moving a tenant back to
/// <see cref="SubscriptionStatus.Active"/> once every overdue invoice is actually cleared happens the
/// moment a payment clears it, in <see cref="PaymentService.RecordPaymentAsync"/> itself, rather than
/// waiting out this sweep's next hourly pass - a paying customer's servers should come back the moment
/// they've paid, not up to an hour later.
/// </para>
/// <para>
/// Neither <see cref="Invoice"/> nor <see cref="Subscription"/> is tenant-scoped (see their own
/// remarks) - a background sweep has no ambient tenant to filter by in the first place, and every query
/// below reaches across every tenant on that same basis <see cref="SubscriptionScheduleService"/>
/// already does.
/// </para>
/// </remarks>
public class DunningService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<DunningService> logger) : BackgroundService
{
    /// <summary>
    /// Hourly - nothing here is latency-sensitive on the scale of days-long grace periods, and matches
    /// <see cref="SubscriptionScheduleService"/>'s own cadence.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad pass kill the loop - see SubscriptionScheduleService's identical
                // reasoning: the next pass retries from current state regardless of what this one managed.
                logger.LogError(ex, "Dunning pass failed; will retry in {Interval}.", Interval);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// One sweep: sends due-soon reminders, marks newly-overdue Organizations past due, and suspends
    /// whichever past-due Organizations have run out their grace period - in that order, so an
    /// Organization that just crossed its due date this very pass gets the PastDue notice now rather
    /// than waiting a full interval, and one whose grace period expires this pass is suspended in the
    /// same run rather than one more no-op pass later.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can drive it directly against an injected clock, the same
    /// reasoning as <see cref="SubscriptionScheduleService.RunPassAsync"/>.
    /// </remarks>
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var communicationPublisher = scope.ServiceProvider.GetRequiredService<ICommunicationPublisher>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IOrganizationLifecycleService>();

        var now = timeProvider.GetUtcNow();
        var reminderDays = await SettingDaysAsync(
            dbContext, PlatformSettingsRegistry.PaymentDueSoonReminderDays,
            PlatformSettingsRegistry.DefaultPaymentDueSoonReminderDays, cancellationToken);
        var graceDays = await SettingDaysAsync(
            dbContext, PlatformSettingsRegistry.SuspensionGraceDays,
            PlatformSettingsRegistry.DefaultSuspensionGraceDays, cancellationToken);

        await SendDueSoonRemindersAsync(dbContext, communicationPublisher, now, reminderDays, cancellationToken);
        await MarkPastDueAsync(dbContext, lifecycle, now, graceDays, cancellationToken);
        await SuspendOverdueAsync(dbContext, lifecycle, now, graceDays, cancellationToken);
    }

    /// <summary>
    /// Queues <see cref="EmailTemplateRegistry.Codes.PaymentDueSoon"/> for every open invoice whose due
    /// date falls inside the reminder window and hasn't been reminded about yet.
    /// </summary>
    private async Task SendDueSoonRemindersAsync(
        ApiDbContext dbContext, ICommunicationPublisher communicationPublisher, DateTimeOffset now,
        int reminderDays, CancellationToken cancellationToken)
    {
        var reminderWindowEnd = now.AddDays(reminderDays);

        // Tenant.DeletedOn == null, same guard PaymentService.GetInvoicesAsync uses - a cancelled
        // (soft-deleted) Organization's old invoice shouldn't generate a "your payment is coming due"
        // email for an account that isn't open any more.
        var due = await dbContext.Set<Invoice>()
            .Include(i => i.Tenant)
            .Where(i => i.Status == InvoiceStatus.Open
                && i.DueSoonReminderSentOn == null
                && i.DueOn != null && i.DueOn > now && i.DueOn <= reminderWindowEnd
                && i.Tenant.DeletedOn == null)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return;
        }

        var sent = 0;
        foreach (var invoice in due)
        {
            // Stamped either way - a tenant with no ContactEmail on file gets no email (same as every
            // other notice in this subsystem - see OrganizationLifecycleService.NotifyAsync), but
            // marking it sent anyway stops this invoice being re-evaluated every pass for the rest of
            // the reminder window purely to discover the same missing address again.
            if (!string.IsNullOrWhiteSpace(invoice.Tenant.ContactEmail))
            {
                await communicationPublisher.QueueTemplatedAsync(
                    EmailTemplateRegistry.Codes.PaymentDueSoon,
                    new Dictionary<string, string>
                    {
                        [EmailTemplateRegistry.Placeholders.OrganizationName] = invoice.Tenant.Name,
                        [EmailTemplateRegistry.Placeholders.InvoiceNumber] = invoice.Number ?? string.Empty,
                        [EmailTemplateRegistry.Placeholders.AmountDue] = invoice.AmountOutstanding.ToString("C"),
                        [EmailTemplateRegistry.Placeholders.DueDate] = invoice.DueOn!.Value.ToString("d MMM yyyy")
                    },
                    invoice.Tenant.ContactEmail, userId: null, invoice.TenantId,
                    cancellationToken: cancellationToken);
                sent++;
            }

            invoice.DueSoonReminderSentOn = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Sent {Sent} payment-due-soon reminder(s) ({Total} invoice(s) marked reminded).", sent, due.Count);
    }

    /// <summary>
    /// Moves every Active Organization with at least one overdue open invoice to
    /// <see cref="SubscriptionStatus.PastDue"/>, quoting the date its service will be suspended if it
    /// stays unpaid.
    /// </summary>
    private async Task MarkPastDueAsync(
        ApiDbContext dbContext, IOrganizationLifecycleService lifecycle, DateTimeOffset now, int graceDays,
        CancellationToken cancellationToken)
    {
        var overdue = await dbContext.Set<Invoice>()
            .Where(i => i.Status == InvoiceStatus.Open && i.DueOn != null && i.DueOn <= now
                && i.Tenant.DeletedOn == null)
            .GroupBy(i => i.TenantId)
            .Select(g => new { TenantId = g.Key, EarliestDueOn = g.Min(i => i.DueOn!.Value) })
            .ToListAsync(cancellationToken);

        if (overdue.Count == 0)
        {
            return;
        }

        var overdueTenantIds = overdue.Select(o => o.TenantId).ToList();

        // Only a currently-Active subscription needs marking - one already PastDue or Suspended is
        // either this same pass's own earlier work moments ago, or an Organization this sweep has
        // already been telling about for a while; SetStatusAsync would no-op on it regardless, but
        // filtering here avoids recomputing (and re-logging) a "suspension in N days" reason nobody's
        // notice will actually change.
        var activeTenantIds = await dbContext.Set<Subscription>()
            .Where(s => s.EndDate == null && s.Status == SubscriptionStatus.Active
                && overdueTenantIds.Contains(s.TenantId))
            .Select(s => s.TenantId)
            .ToListAsync(cancellationToken);

        foreach (var tenantId in activeTenantIds)
        {
            var earliestDueOn = overdue.First(o => o.TenantId == tenantId).EarliestDueOn;
            var suspendOn = now.AddDays(graceDays);
            var reason =
                $"Payment unsuccessful - the invoice due {earliestDueOn:d MMM yyyy} has not been paid. " +
                $"Service will be suspended on {suspendOn:d MMM yyyy} if it remains unpaid.";

            await lifecycle.SetStatusAsync(tenantId, SubscriptionStatus.PastDue, reason, cancellationToken);
        }

        if (activeTenantIds.Count > 0)
        {
            logger.LogInformation("Marked {Count} Organization(s) past due.", activeTenantIds.Count);
        }
    }

    /// <summary>
    /// Suspends every Organization that has sat <see cref="SubscriptionStatus.PastDue"/> for at least
    /// <paramref name="graceDays"/> - the countdown <see cref="MarkPastDueAsync"/>'s own notice quoted.
    /// </summary>
    private async Task SuspendOverdueAsync(
        ApiDbContext dbContext, IOrganizationLifecycleService lifecycle, DateTimeOffset now, int graceDays,
        CancellationToken cancellationToken)
    {
        var graceExpiredBefore = now.AddDays(-graceDays);

        // StatusChangedOn is set the moment SetStatusAsync moves a subscription onto PastDue in the
        // first place, so it's never actually null here - the ?? guard is defensive against a row that
        // somehow predates that invariant, and resolves it to "never expired" (excluded) rather than
        // "always expired" (an unexplained surprise suspension), which is the direction worth erring in
        // for the one action in this whole subsystem that visibly breaks a paying customer's servers.
        var expired = await dbContext.Set<Subscription>()
            .Where(s => s.EndDate == null && s.Status == SubscriptionStatus.PastDue
                && (s.StatusChangedOn ?? DateTimeOffset.MaxValue) <= graceExpiredBefore)
            .Select(s => s.TenantId)
            .ToListAsync(cancellationToken);

        foreach (var tenantId in expired)
        {
            await lifecycle.SetStatusAsync(
                tenantId, SubscriptionStatus.Suspended,
                $"Still unpaid {graceDays} day(s) after being marked past due.", cancellationToken);
        }

        if (expired.Count > 0)
        {
            logger.LogInformation("Suspended {Count} Organization(s) for non-payment.", expired.Count);
        }
    }

    private static async Task<int> SettingDaysAsync(
        ApiDbContext dbContext, string key, int fallback, CancellationToken cancellationToken)
    {
        var raw = await dbContext.Set<PlatformSetting>()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        // Same fallback shape as InvoiceService.PaymentTermsDaysAsync - a missing or nonsensical
        // setting falls back rather than failing a whole sweep over it.
        return int.TryParse(raw, out var days) && days > 0 ? days : fallback;
    }
}
