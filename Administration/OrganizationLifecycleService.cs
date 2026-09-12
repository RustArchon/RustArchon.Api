// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IOrganizationLifecycleService" />
public class OrganizationLifecycleService(
    ApiDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    ICommunicationPublisher communicationPublisher,
    TimeProvider timeProvider,
    ILogger<OrganizationLifecycleService> logger) : IOrganizationLifecycleService
{
    /// <inheritdoc />
    public async Task<bool> SetStatusAsync(
        Guid tenantId, SubscriptionStatus status, string? reason,
        CancellationToken cancellationToken = default)
    {
        if (status == SubscriptionStatus.Cancelled)
        {
            // Cancelling is not a status change - it closes the subscription and retires the tenant -
            // so it has its own method rather than being a value this one accepts.
            throw new ArgumentOutOfRangeException(
                nameof(status), "Use CancelAsync to end an Organization.");
        }

        var subscription = await dbContext.Set<Subscription>()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EndDate == null, cancellationToken);

        if (subscription is null)
        {
            return false;
        }

        var was = subscription.Status;
        if (was == status)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow();
        subscription.Status = status;
        subscription.StatusChangedOn = now;
        subscription.StatusReason = reason;

        await dbContext.SaveChangesAsync(cancellationToken);

        // Only the crossing in or out of Suspended touches connections. PastDue and Active differ in
        // what they say about the money, not about whether the servers run - see the interface.
        if (status == SubscriptionStatus.Suspended)
        {
            var stopped = await StopServersAsync(tenantId, cancellationToken);
            logger.LogWarning(
                "Organization {TenantId} suspended ({Was} -> {Status}); {Count} server connection(s) "
                + "stopped. Reason: {Reason}",
                tenantId, was, status, stopped, reason ?? "none given");

            await NotifyAsync(
                tenantId, EmailTemplateRegistry.Codes.SubscriptionSuspended, reason, cancellationToken);
        }
        else if (was == SubscriptionStatus.Suspended)
        {
            var restored = await RestoreServersAsync(tenantId, cancellationToken);
            logger.LogWarning(
                "Organization {TenantId} reinstated ({Was} -> {Status}); {Count} server connection(s) "
                + "asked to reconnect. Reason: {Reason}",
                tenantId, was, status, restored, reason ?? "none given");

            if (status == SubscriptionStatus.Active)
            {
                await NotifyAsync(
                    tenantId, EmailTemplateRegistry.Codes.SubscriptionReactivated, reason: null, cancellationToken);
            }
        }
        else
        {
            logger.LogInformation(
                "Organization {TenantId} moved {Was} -> {Status}. Reason: {Reason}",
                tenantId, was, status, reason ?? "none given");

            if (status == SubscriptionStatus.PastDue)
            {
                await NotifyAsync(
                    tenantId, EmailTemplateRegistry.Codes.SubscriptionPastDue, reason, cancellationToken);
            }
            else if (status == SubscriptionStatus.Active && was == SubscriptionStatus.PastDue)
            {
                await NotifyAsync(
                    tenantId, EmailTemplateRegistry.Codes.SubscriptionReactivated, reason: null, cancellationToken);
            }
        }

        return true;
    }

    /// <summary>
    /// Queues one of the <see cref="Codes"/>-templated status-change notices to a tenant's own
    /// <c>ContactEmail</c>.
    /// </summary>
    /// <returns><c>false</c>, not an error, for a tenant with no <c>ContactEmail</c> on file, since not
    /// every Organization has necessarily set one.</returns>
    /// <remarks>
    /// A bare <c>IgnoreQueryFilters()</c>, not <see cref="JumpStartQueryableExtensions.AcrossAllTenants{TEntity}"/>
    /// - <see cref="CancelAsync"/> calls this after already soft-deleting the tenant, so the notice for
    /// the cancellation that just happened needs to see it despite that, not just across tenants. See
    /// the "deliberately no helper for dropping both at once" remark on <c>JumpStartQueryFilters</c>.
    /// </remarks>
    private async Task<bool> NotifyAsync(
        Guid tenantId, string templateCode, string? reason, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Set<Tenant>()
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Name, t.ContactEmail })
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(tenant?.ContactEmail))
        {
            return false;
        }

        await communicationPublisher.QueueTemplatedAsync(
            templateCode,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.OrganizationName] = tenant.Name,
                [EmailTemplateRegistry.Placeholders.Reason] = reason ?? string.Empty
            },
            tenant.ContactEmail, userId: null, tenantId, cancellationToken: cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        Guid tenantId, CancellationReasonCategory category, string? reason,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var subscription = await dbContext.Set<Subscription>()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EndDate == null, cancellationToken);

        var tenant = await dbContext.Set<Tenant>()
            .IncludingDeleted()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return false;
        }

        if (subscription is not null)
        {
            subscription.EndDate = now;
            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.StatusChangedOn = now;
            subscription.StatusReason = reason;
        }

        // A queued plan change on an account that is closing would otherwise sit pending forever, and
        // would be applied if the account were ever reopened.
        var queued = await dbContext.Set<ScheduledPlanChange>()
            .Where(c => c.TenantId == tenantId && c.AppliedOn == null && c.CancelledOn == null)
            .ToListAsync(cancellationToken);

        foreach (var change in queued)
        {
            change.CancelledOn = now;
        }

        tenant.IsActive = false;
        tenant.DeletedOn = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        var stopped = await StopServersAsync(tenantId, cancellationToken);

        logger.LogWarning(
            "Organization {TenantId} cancelled ({Category}); {Stopped} server connection(s) stopped, "
            + "{Queued} queued change(s) dropped. Reason: {Reason}",
            tenantId, category, stopped, queued.Count, reason ?? "none given");

        // A cancellation specifically for a Terms of Service violation gets that template instead of
        // the ordinary one - see CancellationReasonCategory's remarks.
        var templateCode = category == CancellationReasonCategory.TosViolation
            ? EmailTemplateRegistry.Codes.TosViolationNotice
            : EmailTemplateRegistry.Codes.SubscriptionCancelled;

        await NotifyAsync(tenantId, templateCode, reason, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReopenAsync(
        Guid tenantId, Guid planId, int termMonths, CancellationToken cancellationToken = default)
    {
        if (!BillingTerms.IsValid(termMonths))
        {
            throw new ArgumentOutOfRangeException(nameof(termMonths), "That is not a billing term.");
        }

        var tenant = await dbContext.Set<Tenant>()
            .IncludingDeleted()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return false;
        }

        var alreadyOpen = await dbContext.Set<Subscription>()
            .AnyAsync(s => s.TenantId == tenantId && s.EndDate == null, cancellationToken);

        if (alreadyOpen)
        {
            return false;
        }

        var plan = await dbContext.Set<Plan>()
            .Include(p => p.Prices)
            .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

        if (plan is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        tenant.IsActive = true;
        tenant.DeletedOn = null;
        tenant.DeletedById = null;

        var serverCount = await dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .CountAsync(s => s.TenantId == tenantId, cancellationToken);

        var quantity = PlanChangeCalculator.ResolveQuantity(plan, termMonths, requested: null, serverCount);
        var periodEnd = now.AddMonths(termMonths);

        var subscription = new Subscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            StartDate = now,
            Status = SubscriptionStatus.Active,
            StatusChangedOn = now,
            StatusReason = "Reopened by a site admin."
        };
        dbContext.Set<Subscription>().Add(subscription);
        await dbContext.SaveChangesAsync(cancellationToken);

        // A fresh period, priced at list. Deliberately not invoiced here: reopening an account is a
        // conversation that has already happened, and whoever had it decides what the customer owes -
        // silently raising a document for a full period would pre-empt that. The next renewal bills
        // normally.
        dbContext.Set<SubscriptionPeriod>().Add(new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            TermMonths = termMonths,
            Quantity = quantity,
            PeriodStart = now,
            PeriodEnd = periodEnd,
            StartDate = now,
            EndDate = periodEnd,
            EarnedAmount = PlanChangeCalculator.PriceFor(plan, termMonths, quantity)
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        var restored = await RestoreServersAsync(tenantId, cancellationToken);

        logger.LogWarning(
            "Organization {TenantId} reopened on '{Plan}' ({Term} month term); {Count} server "
            + "connection(s) asked to reconnect. Not invoiced - see ReopenAsync.",
            tenantId, plan.Name, termMonths, restored);

        if (!string.IsNullOrWhiteSpace(tenant.ContactEmail))
        {
            await communicationPublisher.QueueTemplatedAsync(
                EmailTemplateRegistry.Codes.SubscriptionReopened,
                new Dictionary<string, string>
                {
                    [EmailTemplateRegistry.Placeholders.OrganizationName] = tenant.Name,
                    [EmailTemplateRegistry.Placeholders.PlanName] = plan.Name
                },
                tenant.ContactEmail, userId: null, tenantId, cancellationToken: cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Tears down every live connection this Organization has, using the same per-server message the
    /// customer's own disable button sends.
    /// </summary>
    private async Task<int> StopServersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var serverIds = await ServerIdsAsync(tenantId, enabledOnly: false, cancellationToken);

        foreach (var serverId in serverIds)
        {
            await publishEndpoint.Publish(
                new ServerLifecycleChanged(serverId, tenantId, ServerLifecycleChangeType.Disabled),
                cancellationToken);
        }

        return serverIds.Count;
    }

    /// <summary>
    /// Asks for the Organization's connections back - only for the servers the customer has enabled,
    /// which is why <c>IsEnabled</c> was never touched on the way down.
    /// </summary>
    private async Task<int> RestoreServersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var serverIds = await ServerIdsAsync(tenantId, enabledOnly: true, cancellationToken);

        foreach (var serverId in serverIds)
        {
            await publishEndpoint.Publish(new ConnectToServer(serverId, tenantId), cancellationToken);
        }

        return serverIds.Count;
    }

    private async Task<List<Guid>> ServerIdsAsync(
        Guid tenantId, bool enabledOnly, CancellationToken cancellationToken)
    {
        var query = dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .Where(s => s.TenantId == tenantId);

        if (enabledOnly)
        {
            query = query.Where(s => s.IsEnabled);
        }

        return await query.Select(s => s.Id).ToListAsync(cancellationToken);
    }
}
