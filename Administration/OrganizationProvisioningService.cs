// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using JumpStart.MultiTenant.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IOrganizationProvisioningService" />
public class OrganizationProvisioningService(
    ApiDbContext dbContext,
    ITenantRepository tenantRepository,
    IUserTenantRepository userTenantRepository,
    IRoleRepository roleRepository,
    IPlanRepository planRepository,
    ISubscriptionRepository subscriptionRepository,
    IInvoiceService invoiceService,
    IPlatformSettingsCache settingsCache,
    TimeProvider timeProvider,
    ILogger<OrganizationProvisioningService> logger) : IOrganizationProvisioningService
{
    /// <inheritdoc />
    public async Task<Tenant> CreateAsync(
        Guid userId,
        string? name,
        Guid? planId = null,
        bool enforceOnePerOwner = false,
        CancellationToken cancellationToken = default)
    {
        var plan = await ResolvePlanAsync(planId, cancellationToken);

        if (enforceOnePerOwner
            && await WouldExceedOnePerOwnerAsync(userId, plan.Id, null, cancellationToken))
        {
            throw new OrganizationProvisioningException(
                $"You already have an organization on the {plan.Name} plan, and only one is allowed "
                + "per account. Choose a different plan for this one.");
        }

        var now = timeProvider.GetUtcNow();

        var tenant = await tenantRepository.AddAsync(new Tenant
        {
            Name = string.IsNullOrWhiteSpace(name) ? "My Organization" : name.Trim(),
            IsActive = true,

            // Set explicitly rather than left to the auditing interceptor, because the one-per-owner
            // rule is counted from it - a rule that depends on a field being populated should not
            // also depend on something else remembering to populate it.
            CreatedById = userId,
            CreatedOn = now
        });

        // Every Organization must have a Plan from the moment it exists - see Subscription's remarks.
        var subscription = await subscriptionRepository.AddAsync(new Subscription
        {
            TenantId = tenant.Id,
            PlanId = plan.Id,
            StartDate = now
        });

        // Monthly is the starting term for every new Organization: the shortest commitment, and
        // nothing carries a term choice through creation yet. Moving to a longer one is an ordinary
        // plan change - immediate and prorated - so starting short costs nothing.
        var periodEnd = now.AddMonths(BillingTerms.Monthly);
        var quantity = PlanChangeCalculator.ResolveQuantity(
            plan, BillingTerms.Monthly, requested: null, serverCount: 0);

        var firstPeriod = new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            TermMonths = BillingTerms.Monthly,
            Quantity = quantity,
            PeriodStart = now,
            PeriodEnd = periodEnd,
            StartDate = now,
            EndDate = periodEnd,
            EarnedAmount = PlanChangeCalculator.PriceFor(plan, BillingTerms.Monthly, quantity)
        };

        dbContext.Set<SubscriptionPeriod>().Add(firstPeriod);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Service is paid in advance, so the first period is invoiced like any other. A free plan
        // still produces no document, because IssueForPeriodAsync declines to bill nothing.
        await invoiceService.IssueForPeriodAsync(
            firstPeriod,
            firstPeriod.EarnedAmount,
            $"{plan.Name} - {BillingTerms.Describe(BillingTerms.Monthly)}, "
            + $"{now:d MMM yyyy} - {periodEnd:d MMM yyyy}",
            cancellationToken);

        await userTenantRepository.AddAsync(new UserTenant { UserId = userId, TenantId = tenant.Id });

        await GrantFounderOwnershipAsync(userId, tenant.Id);

        logger.LogInformation(
            "Provisioned organization {TenantId} for user {UserId} on the {PlanName} plan.",
            tenant.Id, userId, plan.Name);

        return tenant;
    }

    /// <inheritdoc />
    public Task<bool> WouldExceedOnePerOwnerAsync(
        Guid userId, Guid planId, Guid? ignoringTenantId = null,
        CancellationToken cancellationToken = default) =>
        OnePerOwnerRule.WouldExceedAsync(dbContext, userId, planId, ignoringTenantId, cancellationToken);

    /// <summary>The requested plan, the platform default, or the cheapest one still on sale.</summary>
    private async Task<Plan> ResolvePlanAsync(Guid? planId, CancellationToken cancellationToken)
    {
        if (planId is { } requested)
        {
            var chosen = await planRepository.GetWithPricesAsync(requested)
                ?? throw new OrganizationProvisioningException("That plan doesn't exist.");

            // Only a plan currently on sale may be chosen. Staying on one that has since been
            // superseded is fine and deliberate; newly picking one is not - the same rule
            // SubscriptionService applies to a plan change.
            return chosen.Active
                ? chosen
                : throw new OrganizationProvisioningException($"The {chosen.Name} plan is no longer available.");
        }

        // The site admin's explicit choice if they made one - honoured even if that plan has since
        // been deactivated, since that is a deliberate decision rather than a stale reference.
        var configured = await settingsCache.GetStringAsync(PlatformSettingsRegistry.DefaultPlanId);

        if (!string.IsNullOrWhiteSpace(configured) && Guid.TryParse(configured, out var defaultPlanId)
            && await planRepository.GetWithPricesAsync(defaultPlanId) is { } configuredPlan)
        {
            return configuredPlan;
        }

        return await planRepository.GetCheapestActiveAsync()
            ?? throw new InvalidOperationException(
                "No usable Plan exists - check PlanSeeder ran and at least one Plan is still active.");
    }

    /// <summary>
    /// Grants the founder the built-in Owner role inside their new Organization.
    /// </summary>
    /// <remarks>
    /// The system variant deliberately: this is the bootstrap case JumpStart's grant rules cannot
    /// express, because the founder holds nothing yet and "you cannot grant what you do not hold"
    /// would refuse the very grant that gives them their first permission. ADR-019 requires that
    /// exception be named rather than inferred, which is what the method name does.
    /// </remarks>
    private async Task GrantFounderOwnershipAsync(Guid userId, Guid tenantId)
    {
        var ownerRoleId = await dbContext.Set<Role>()
            .AcrossAllTenants()
            .Where(r => r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        if (ownerRoleId == Guid.Empty)
        {
            // Seeded at startup, so its absence means that did not run. Failing loudly beats
            // provisioning an Organization whose founder can do nothing in it.
            throw new InvalidOperationException(
                "The built-in Owner role is missing - check BuiltInRoleSeeder ran at startup.");
        }

        await roleRepository.AssignUserToRoleAsSystemAsync(userId, ownerRoleId, tenantId);
    }
}
