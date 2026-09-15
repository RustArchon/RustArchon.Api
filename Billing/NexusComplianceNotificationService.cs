// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Billing;

/// <summary>
/// Once a day, tells whoever is configured to hear about it that one or more tenants' invoices are
/// blocked on a missing Stripe tax registration - see <see cref="BlockedInvoiceIssuance"/>, the table
/// <see cref="Infrastructure.SubscriptionScheduleService"/> writes every time
/// <see cref="TaxJurisdictionUnregisteredException"/> is caught.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One digest a day, not one email per blocked invoice.</strong> A jurisdiction that stays
/// unregistered blocks the same handful of tenants every single hourly pass; emailing about each of
/// those passes individually would be dozens of near-identical emails a day for one still-open problem.
/// This service instead reads whatever is in <see cref="BlockedInvoiceIssuance"/> right now - the same
/// table the admin banner reads - and sends one summary grouped by jurisdiction.
/// </para>
/// <para>
/// <strong>A separate service from <see cref="Infrastructure.SubscriptionScheduleService"/> and
/// <see cref="DunningService"/></strong>, for the same reason those two are separate from each other:
/// this is neither "does a subscription's period need to move forward" nor "has an invoice gone
/// unpaid" - it is "does a site admin need to go register somewhere in Stripe's Dashboard", a
/// different question on a different (daily, not hourly) cadence.
/// </para>
/// <para>
/// <strong>Sent to a configured address, not "every Site Admin".</strong> See
/// <see cref="PlatformSettingsRegistry.ComplianceNotificationEmail"/>'s own remarks: this Api has no way
/// to resolve a Site Admin's user id to an email address at all, since Identity - and every user's email
/// - lives entirely in RustArchon.Panel. An empty setting means the digest is skipped outright, checked
/// first so a deployment that hasn't configured one yet never queries <see cref="BlockedInvoiceIssuance"/>
/// for nothing.
/// </para>
/// </remarks>
public class NexusComplianceNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<NexusComplianceNotificationService> logger) : BackgroundService
{
    /// <summary>
    /// Daily - a jurisdiction being blocked is a slow-moving, human-actioned problem (register in a
    /// Dashboard), nothing like the hourly billing/dunning sweeps this is meant to complement rather
    /// than duplicate.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

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
                // Never let one bad pass kill the loop - see DunningService/SubscriptionScheduleService's
                // identical reasoning.
                logger.LogError(ex, "Nexus compliance notification pass failed; will retry in {Interval}.", Interval);
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
    /// Internal rather than private so a test can drive it directly, the same reasoning as
    /// <see cref="Infrastructure.SubscriptionScheduleService.RunPassAsync"/>.
    /// </summary>
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var communicationPublisher = scope.ServiceProvider.GetRequiredService<ICommunicationPublisher>();

        var toAddress = await dbContext.Set<PlatformSetting>()
            .Where(s => s.Key == PlatformSettingsRegistry.ComplianceNotificationEmail)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(toAddress))
        {
            return;
        }

        var blocked = await dbContext.Set<BlockedInvoiceIssuance>().ToListAsync(cancellationToken);

        if (blocked.Count == 0)
        {
            return;
        }

        var byJurisdiction = blocked
            .GroupBy(b => (b.Country, b.State))
            .OrderBy(g => g.Min(b => b.FirstBlockedOn))
            .ToList();

        var listItems = string.Join(Environment.NewLine, byJurisdiction.Select(g =>
        {
            var jurisdiction = g.Key.State is { Length: > 0 } state
                ? $"{state}, {g.Key.Country}"
                : g.Key.Country;
            var count = g.Count();

            return "<li><strong>" + WebUtility.HtmlEncode(jurisdiction) + "</strong> - "
                + count + (count == 1 ? " organization" : " organizations")
                + " blocked, oldest since " + g.Min(b => b.FirstBlockedOn).ToString("d MMM yyyy") + "</li>";
        }));

        var organizationCount = blocked.Select(b => b.TenantId).Distinct().Count();

        await communicationPublisher.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.TaxRegistrationNeeded,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.BlockedOrganizationCount] = organizationCount.ToString(),
                [EmailTemplateRegistry.Placeholders.BlockedJurisdictionList] = listItems
            },
            toAddress, userId: null, tenantId: null, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Sent tax registration digest for {JurisdictionCount} jurisdiction(s), {OrganizationCount} organization(s).",
            byJurisdiction.Count, organizationCount);
    }
}
