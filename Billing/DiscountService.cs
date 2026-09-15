// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <summary>The outcome of a redemption attempt - a plain reason a self-service or admin caller can
/// show directly, never a thrown exception, since "that code doesn't work" is an ordinary, expected
/// outcome rather than a failure of the system.</summary>
public sealed record DiscountRedemptionResult(bool Success, string? ErrorMessage, DiscountRedemption? Redemption)
{
    public static DiscountRedemptionResult Fail(string message) => new(false, message, null);
    public static DiscountRedemptionResult Ok(DiscountRedemption redemption) => new(true, null, redemption);
}

/// <summary>
/// Managing the discount catalog, and redeeming a code against one Organization.
/// </summary>
public interface IDiscountService
{
    /// <summary><paramref name="code"/> is normalised (trimmed, upper-cased) before being stored - see
    /// <see cref="RedeemAsync"/>'s matching normalisation.</summary>
    Task<Discount> CreateAsync(
        string code, DiscountAmountType amountType, decimal amountValue, string? description,
        int? maxRedemptions, bool oncePerOrganization, Guid? restrictedToTenantId, DateTimeOffset? expiresOn,
        CancellationToken cancellationToken = default);

    /// <summary>Every discount ever created, newest first.</summary>
    Task<IReadOnlyList<Discount>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>An admin's manual on/off switch - see <see cref="Discount.IsActive"/>.</summary>
    Task<bool> SetActiveAsync(Guid discountId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates <paramref name="code"/> against every rule on the matching <see cref="Discount"/> and,
    /// if it passes, reserves a <see cref="DiscountRedemptionStatus.Pending"/> redemption for
    /// <paramref name="tenantId"/> - applied the next time an invoice is issued for that tenant (see
    /// <see cref="InvoiceService.IssueForPeriodAsync"/>).
    /// </summary>
    /// <remarks>
    /// The one caller for both self-service (<c>SubscriptionController</c>, the tenant redeeming its own
    /// code) and admin-assignment (<c>DiscountsController</c>, an admin redeeming a
    /// <see cref="Discount.RestrictedToTenantId"/>-targeted code on a customer's behalf) - the rules are
    /// exactly the same either way, so there is exactly one place they can drift from each other.
    /// </remarks>
    Task<DiscountRedemptionResult> RedeemAsync(
        Guid tenantId, string code, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDiscountService" />
/// <remarks>
/// <strong>Redemption count increments at reservation time, not at spend time.</strong> A
/// <see cref="DiscountRedemptionStatus.Pending"/> redemption already counts against
/// <see cref="Discount.MaxRedemptions"/> the moment it's created, before any invoice exists to apply it
/// to - otherwise a capped code could be over-committed by more tenants redeeming it than it actually
/// allows, all still waiting on their next invoice.
/// </remarks>
/// <remarks>
/// <strong>No row-locking against a concurrent redemption of the very last slot on a capped code.</strong>
/// A genuine race here costs, at worst, one discount too many actually being honoured - a minor revenue
/// leak, not a data-integrity problem - and is far less likely in practice than
/// <c>InvoiceService.TakeNumberAsync</c>'s gapless-sequence requirement, which is why that one uses
/// <c>SELECT ... FOR UPDATE</c> and this doesn't. Worth revisiting if it ever actually happens.
/// </remarks>
public class DiscountService(ApiDbContext dbContext, TimeProvider timeProvider, ILogger<DiscountService> logger)
    : IDiscountService
{
    /// <inheritdoc />
    public async Task<Discount> CreateAsync(
        string code, DiscountAmountType amountType, decimal amountValue, string? description,
        int? maxRedemptions, bool oncePerOrganization, Guid? restrictedToTenantId, DateTimeOffset? expiresOn,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A discount needs a code.", nameof(code));
        }

        if (amountValue <= 0m || (amountType == DiscountAmountType.PercentOff && amountValue > 100m))
        {
            throw new ArgumentOutOfRangeException(nameof(amountValue), "That amount isn't valid for this discount type.");
        }

        var normalizedCode = Normalize(code);

        if (await dbContext.Set<Discount>().AnyAsync(d => d.Code == normalizedCode, cancellationToken))
        {
            throw new InvalidOperationException($"A discount with code '{normalizedCode}' already exists.");
        }

        var discount = new Discount
        {
            Code = normalizedCode,
            Description = description,
            AmountType = amountType,
            AmountValue = amountValue,
            MaxRedemptions = maxRedemptions,
            OncePerOrganization = oncePerOrganization,
            RestrictedToTenantId = restrictedToTenantId,
            ExpiresOn = expiresOn,
            CreatedOn = timeProvider.GetUtcNow()
        };
        dbContext.Set<Discount>().Add(discount);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created discount '{Code}' ({AmountType} {AmountValue}).", normalizedCode, amountType, amountValue);
        return discount;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Discount>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Set<Discount>()
            .Include(d => d.RestrictedToTenant)
            .OrderByDescending(d => d.CreatedOn)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(Guid discountId, bool isActive, CancellationToken cancellationToken = default)
    {
        var discount = await dbContext.Set<Discount>().FirstOrDefaultAsync(d => d.Id == discountId, cancellationToken);
        if (discount is null)
        {
            return false;
        }

        discount.IsActive = isActive;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<DiscountRedemptionResult> RedeemAsync(
        Guid tenantId, string code, CancellationToken cancellationToken = default)
    {
        var normalizedCode = Normalize(code);

        var discount = await dbContext.Set<Discount>()
            .FirstOrDefaultAsync(d => d.Code == normalizedCode, cancellationToken);

        if (discount is null)
        {
            return DiscountRedemptionResult.Fail("That code doesn't exist.");
        }

        if (!discount.IsActive)
        {
            return DiscountRedemptionResult.Fail("That code is no longer active.");
        }

        if (discount.ExpiresOn is { } expiresOn && expiresOn <= timeProvider.GetUtcNow())
        {
            return DiscountRedemptionResult.Fail("That code has expired.");
        }

        if (discount.MaxRedemptions is { } max && discount.TimesRedeemed >= max)
        {
            return DiscountRedemptionResult.Fail("That code has already been fully redeemed.");
        }

        if (discount.RestrictedToTenantId is { } restrictedTo && restrictedTo != tenantId)
        {
            return DiscountRedemptionResult.Fail("That code isn't valid for this account.");
        }

        // "One coupon per order/renewal" - refused outright rather than queued, so it's never ambiguous
        // which discount a given invoice was reduced by.
        var alreadyPending = await dbContext.Set<DiscountRedemption>()
            .AnyAsync(r => r.TenantId == tenantId && r.Status == DiscountRedemptionStatus.Pending, cancellationToken);
        if (alreadyPending)
        {
            return DiscountRedemptionResult.Fail(
                "This account already has a discount waiting to be applied to its next invoice.");
        }

        if (discount.OncePerOrganization)
        {
            var alreadyRedeemedByThisTenant = await dbContext.Set<DiscountRedemption>()
                .AnyAsync(r => r.DiscountId == discount.Id && r.TenantId == tenantId, cancellationToken);
            if (alreadyRedeemedByThisTenant)
            {
                return DiscountRedemptionResult.Fail("This account has already used that code.");
            }
        }

        var redemption = new DiscountRedemption
        {
            DiscountId = discount.Id,
            TenantId = tenantId,
            Status = DiscountRedemptionStatus.Pending,
            RedeemedOn = timeProvider.GetUtcNow()
        };
        dbContext.Set<DiscountRedemption>().Add(redemption);
        discount.TimesRedeemed++;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Tenant {TenantId} redeemed discount '{Code}' ({DiscountId}) - pending against its next invoice.",
            tenantId, discount.Code, discount.Id);

        return DiscountRedemptionResult.Ok(redemption);
    }

    private static string Normalize(string code) => code.Trim().ToUpperInvariant();
}
