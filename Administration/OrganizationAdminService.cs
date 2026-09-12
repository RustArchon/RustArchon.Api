// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IOrganizationAdminService" />
public class OrganizationAdminService(
    ApiDbContext dbContext, IPaymentService paymentService, INoteRepository notes, IUserContext userContext)
    : IOrganizationAdminService
{
    /// <summary>
    /// How many billing periods the detail page carries back. Enough to see the shape of an account's
    /// history without shipping five years of monthly rows to render a page nobody scrolls that far down.
    /// </summary>
    private const int PeriodHistoryLimit = 24;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganizationSummaryDto>> ListAsync(
        OrganizationQueryDto query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Cancelled Organizations are soft-deleted, so seeing them at all means deliberately dropping
        // that filter - and only that one. See JumpStartQueryableExtensions.
        var tenants = query.IncludeCancelled
            ? dbContext.Set<Tenant>().IncludingDeleted()
            : dbContext.Set<Tenant>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = $"%{query.Search.Trim()}%";
            tenants = tenants.Where(t =>
                EF.Functions.ILike(t.Name, term)
                || (t.ContactEmail != null && EF.Functions.ILike(t.ContactEmail, term)));
        }

        var rows = await tenants
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.ContactEmail,
                t.CreatedOn,
                t.DeletedOn
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var tenantIds = rows.Select(r => r.Id).ToList();
        var context = await LoadContextAsync(tenantIds, cancellationToken);

        var summaries = rows
            .Select(row => BuildSummary(
                row.Id, row.Name, row.ContactEmail, row.CreatedOn, row.DeletedOn, context))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        if (query.PlanId is { } planId)
        {
            var planName = await dbContext.Set<Plan>()
                .Where(p => p.Id == planId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken);

            // Matched by name rather than by id on purpose: a superseded plan is a different row with the
            // same name (see Plan's remarks), and an admin filtering for "Metal" means every customer on
            // a Metal, not only those on today's version of it.
            summaries = summaries.Where(s => s.PlanName == planName).ToList();
        }

        if (query.Status is { } status)
        {
            summaries = summaries.Where(s => s.Status == status).ToList();
        }

        if (query.MinimumOutstanding > 0m)
        {
            summaries = summaries.Where(s => s.Outstanding >= query.MinimumOutstanding).ToList();
        }

        return summaries
            .OrderByDescending(s => s.CreatedOn)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<OrganizationDetailDto?> GetAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Set<Tenant>()
            .IncludingDeleted()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.Name, t.ContactEmail, t.CreatedOn, t.DeletedOn })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        var context = await LoadContextAsync([tenantId], cancellationToken);
        var summary = BuildSummary(
            tenant.Id, tenant.Name, tenant.ContactEmail, tenant.CreatedOn, tenant.DeletedOn, context);

        if (summary is null)
        {
            // A tenant with no subscription at all. SubscriptionBackfiller exists to make this
            // impossible, so reaching it means something is wrong rather than that the account is new -
            // surfacing the account with an empty plan is more useful than a 404 that hides it.
            summary = new OrganizationSummaryDto
            {
                Id = tenant.Id,
                Name = tenant.Name,
                ContactEmail = tenant.ContactEmail,
                CreatedOn = tenant.CreatedOn,
                CancelledOn = tenant.DeletedOn,
                PlanName = "(none)",
                Status = SubscriptionStatus.Cancelled
            };
        }

        var intervals = await dbContext.Set<Subscription>()
            .Where(s => s.TenantId == tenantId)
            .Include(s => s.Plan)
            .OrderByDescending(s => s.StartDate)
            .Select(s => new OrganizationIntervalDto
            {
                Id = s.Id,
                PlanName = s.Plan.Name,
                PlanColorCode = s.Plan.ColorCode,
                StartDate = s.StartDate,
                EndDate = s.EndDate,
                Status = s.Status,
                PeriodCount = s.Periods.Count
            })
            .ToListAsync(cancellationToken);

        // Which invoice billed which span. The join runs through InvoiceLine because that is the single
        // link between the subscription band and the billing band - see InvoiceLine's remarks.
        var billed = await dbContext.Set<InvoiceLine>()
            .Where(l => l.SubscriptionPeriodId != null
                && l.Invoice.TenantId == tenantId
                && l.Invoice.Status != InvoiceStatus.Void)
            .Select(l => new { PeriodId = l.SubscriptionPeriodId!.Value, l.Invoice.Number })
            .ToListAsync(cancellationToken);

        var billedByPeriod = billed
            .GroupBy(b => b.PeriodId)
            .ToDictionary(g => g.Key, g => g.First().Number);

        var periods = await dbContext.Set<SubscriptionPeriod>()
            .Where(p => p.Subscription.TenantId == tenantId)
            .OrderByDescending(p => p.StartDate)
            .Take(PeriodHistoryLimit)
            .Select(p => new OrganizationPeriodDto
            {
                Id = p.Id,
                PlanName = p.Subscription.Plan.Name,
                TermMonths = p.TermMonths,
                Quantity = p.Quantity,
                StartDate = p.StartDate,
                EndDate = p.EndDate,
                PeriodStart = p.PeriodStart,
                PeriodEnd = p.PeriodEnd,
                EarnedAmount = p.EarnedAmount,
                IsWholePeriod = p.StartDate == p.PeriodStart && p.EndDate == p.PeriodEnd
            })
            .ToListAsync(cancellationToken);

        foreach (var period in periods)
        {
            period.InvoiceNumber = billedByPeriod.GetValueOrDefault(period.Id);
        }

        var servers = await dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .Select(s => new OrganizationServerDto
            {
                Id = s.Id,
                Name = s.Name,
                Host = s.Host,
                Port = s.Port,
                Description = s.Description,
                IsEnabled = s.IsEnabled,
                ConnectionStatus = s.ConnectionStatus,
                ConnectionStatusDetail = s.ConnectionStatusDetail,
                ConnectionStatusChangedAtUtc = s.ConnectionStatusChangedAtUtc,
                AssignedWorkerId = s.AssignedWorkerId,
                LastHeartbeatUtc = s.LastHeartbeatUtc,
                CreatedOn = s.CreatedOn
            })
            .ToListAsync(cancellationToken);

        // Straight through the billing service rather than a second mapping of the same rows: it already
        // carries the lines, the settlements and the derived overdue figure the admin invoice screen
        // shows, and two mappings of one document is two places for them to disagree.
        var invoices = await paymentService.GetInvoicesAsync(
            status: null, tenantId: tenantId, cancellationToken);

        var members = await LoadMembersAsync(tenantId, cancellationToken);
        var roles = await LoadRolesAsync(tenantId, cancellationToken);

        // Same visibility rule NotesController.List applies - a badge that counted somebody else's
        // private notes would promise the tab holds more than the viewer is actually about to see.
        var currentUserId = await userContext.GetCurrentUserIdAsync();
        var noteCount = (await notes.GetVisibleAsync(tenantId, null, currentUserId, cancellationToken)).Count;

        var pending = await dbContext.Set<ScheduledPlanChange>()
            .Where(c => c.TenantId == tenantId && c.AppliedOn == null && c.CancelledOn == null)
            .Include(c => c.Plan)
            .Select(c => new OrganizationPendingChangeDto
            {
                Id = c.Id,
                PlanName = c.Plan.Name,
                TermMonths = c.TermMonths,
                Quantity = c.Quantity,
                EffectiveDate = c.EffectiveDate,
                CreatedOn = c.CreatedOn
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new OrganizationDetailDto
        {
            Summary = summary,
            History = intervals,
            Periods = periods,
            Servers = servers,
            Invoices = [.. invoices],
            Members = members,
            Roles = roles,
            NoteCount = noteCount,
            PendingChange = pending
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportFilterOptionDto>> GetPlanOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var subscribed = dbContext.Set<Subscription>().Select(s => s.PlanId);

        var plans = await dbContext.Set<Plan>()
            .Where(p => p.Active || subscribed.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Active })
            .ToListAsync(cancellationToken);

        return plans
            .GroupBy(p => p.Name)
            .Select(g =>
            {
                var current = g.FirstOrDefault(p => p.Active) ?? g.First();
                return new ReportFilterOptionDto { Value = current.Id.ToString(), Label = g.Key };
            })
            .ToList();
    }

    private async Task<List<OrganizationMemberDto>> LoadMembersAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var memberships = await dbContext.Set<UserTenant>()
            .Where(ut => ut.TenantId == tenantId)
            .Select(ut => new { ut.UserId, ut.IsActive, ut.CreatedOn })
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0)
        {
            return [];
        }

        var userIds = memberships.Select(m => m.UserId).ToList();

        // Roles scoped to this Organization only. A platform-wide role (TenantId null - "Site Admin") is
        // deliberately not listed here: it says nothing about this membership, and showing it on a
        // customer's page would read as though the customer held it.
        //
        // AcrossAllTenants because UserRole is ITenantScopedOptional and therefore tenant-filtered. Found
        // the hard way: without it this returns rows for the *admin's own* Organization rather than the
        // one on screen, so every other customer's members appeared to hold no roles at all - a silent
        // wrong answer, not an error.
        var roles = await dbContext.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.TenantId == tenantId && userIds.Contains(ur.UserId))
            .Select(ur => new { ur.UserId, ur.RoleId, ur.Role.Name })
            .ToListAsync(cancellationToken);

        var rolesByUser = roles
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Names: g.Select(r => r.Name).OrderBy(n => n).ToList(),
                    Ids: g.Select(r => r.RoleId).ToList()));

        return memberships
            .Select(m =>
            {
                var held = rolesByUser.GetValueOrDefault(m.UserId, ([], []));

                return new OrganizationMemberDto
                {
                    UserId = m.UserId,
                    IsActive = m.IsActive,
                    JoinedOn = m.CreatedOn,
                    Roles = held.Names,
                    RoleIds = held.Ids
                };
            })
            .OrderBy(m => m.JoinedOn)
            .ToList();
    }

    /// <summary>The roles defined within one Organization, with how many of its members hold each.</summary>
    private async Task<List<OrganizationRoleDto>> LoadRolesAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        // Both AcrossAllTenants, and for the same reason as the member roles above: Role and UserRole are
        // ITenantScopedOptional, so the ambient filter would answer with the admin's own Organization's
        // roles instead of this one's.
        // The built-in Owner is included even though it is not this tenant's row: it is a role the
        // Organization's members hold and a site admin needs to be able to grant, and leaving it out
        // would show an Owner in the members list with no way to give anybody else the same standing.
        var roles = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId == tenantId
                || (r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName))
            .Select(r => new OrganizationRoleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsBuiltIn = r.TenantId == null,
                MemberCount = dbContext.Set<UserRole>()
                    .AcrossAllTenants()
                    .Count(ur => ur.RoleId == r.Id && ur.TenantId == tenantId)
            })
            .ToListAsync(cancellationToken);

        return [.. roles
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The per-tenant facts the summary is assembled from, fetched in one batch per table rather than
    /// per Organization.
    /// </summary>
    private async Task<SummaryContext> LoadContextAsync(
        List<Guid> tenantIds, CancellationToken cancellationToken)
    {
        var subscriptions = await dbContext.Set<Subscription>()
            .Where(s => s.EndDate == null && tenantIds.Contains(s.TenantId))
            .Include(s => s.Plan).ThenInclude(p => p.Prices)
            .ToListAsync(cancellationToken);

        var subscriptionIds = subscriptions.Select(s => s.Id).ToList();

        // The newest slice of each open subscription is its current period - the same "first row of the
        // history" the subscription service treats as current.
        var slices = await dbContext.Set<SubscriptionPeriod>()
            .Where(p => subscriptionIds.Contains(p.SubscriptionId))
            .GroupBy(p => p.SubscriptionId)
            .Select(g => g.OrderByDescending(p => p.StartDate).First())
            .ToListAsync(cancellationToken);

        var serverCounts = await dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .Where(s => tenantIds.Contains(s.TenantId))
            .GroupBy(s => s.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var balances = await dbContext.Set<Invoice>()
            .Where(i => i.Status == InvoiceStatus.Open && tenantIds.Contains(i.TenantId))
            .GroupBy(i => i.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                Outstanding = g.Sum(i => i.Total - i.AmountPaid - i.AmountCredited),
                Count = g.Count()
            })
            .ToListAsync(cancellationToken);

        var memberCounts = await dbContext.Set<UserTenant>()
            .Where(ut => tenantIds.Contains(ut.TenantId))
            .GroupBy(ut => ut.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return new SummaryContext(
            subscriptions.ToDictionary(s => s.TenantId),
            slices.ToDictionary(p => p.SubscriptionId),
            serverCounts.ToDictionary(s => s.TenantId, s => s.Count),
            balances.ToDictionary(b => b.TenantId, b => (b.Outstanding, b.Count)),
            memberCounts.ToDictionary(m => m.TenantId, m => m.Count));
    }

    private static OrganizationSummaryDto? BuildSummary(
        Guid tenantId, string name, string? contactEmail,
        DateTimeOffset createdOn, DateTimeOffset? deletedOn, SummaryContext context)
    {
        var balance = context.Balances.GetValueOrDefault(tenantId, (0m, 0));

        if (!context.Subscriptions.TryGetValue(tenantId, out var subscription))
        {
            // Cancelled Organizations have no open subscription by definition, so they still need a row -
            // the last thing they were on is gone, but the account and anything it still owes are not.
            return deletedOn is null
                ? null
                : new OrganizationSummaryDto
                {
                    Id = tenantId,
                    Name = name,
                    ContactEmail = contactEmail,
                    CreatedOn = createdOn,
                    CancelledOn = deletedOn,
                    PlanName = "(cancelled)",
                    Status = SubscriptionStatus.Cancelled,
                    Outstanding = balance.Item1,
                    OpenInvoiceCount = balance.Item2,
                    ServerCount = context.ServerCounts.GetValueOrDefault(tenantId),
                    MemberCount = context.MemberCounts.GetValueOrDefault(tenantId)
                };
        }

        var slice = context.Slices.GetValueOrDefault(subscription.Id);
        var term = slice?.TermMonths ?? BillingTerms.Monthly;
        var quantity = slice?.Quantity ?? 1;

        return new OrganizationSummaryDto
        {
            Id = tenantId,
            Name = name,
            ContactEmail = contactEmail,
            CreatedOn = createdOn,
            CancelledOn = deletedOn,
            PlanName = subscription.Plan.Name,
            PlanColorCode = subscription.Plan.ColorCode,
            TermMonths = term,
            Status = subscription.Status,
            StatusChangedOn = subscription.StatusChangedOn,
            StatusReason = subscription.StatusReason,
            RenewsOn = slice?.PeriodEnd,
            Quantity = quantity,
            ServerCount = context.ServerCounts.GetValueOrDefault(tenantId),
            MonthlyValue = PlanChangeCalculator.MonthlyEquivalent(subscription.Plan, term, quantity),
            Outstanding = balance.Item1,
            OpenInvoiceCount = balance.Item2,
            MemberCount = context.MemberCounts.GetValueOrDefault(tenantId),
            OnPlanSince = subscription.StartDate
        };
    }

    private sealed record SummaryContext(
        Dictionary<Guid, Subscription> Subscriptions,
        Dictionary<Guid, SubscriptionPeriod> Slices,
        Dictionary<Guid, int> ServerCounts,
        Dictionary<Guid, (decimal, int)> Balances,
        Dictionary<Guid, int> MemberCounts);
}
