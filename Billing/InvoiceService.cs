// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <inheritdoc cref="IInvoiceService" />
public class InvoiceService(
    ApiDbContext dbContext,
    ICommunicationPublisher communicationPublisher,
    IStripeTaxService stripeTax,
    TimeProvider timeProvider,
    ILogger<InvoiceService> logger) : IInvoiceService
{
    /// <inheritdoc />
    public Task<bool> HasBeenBilledAsync(Guid periodId, CancellationToken cancellationToken = default) =>
        dbContext.Set<InvoiceLine>()
            .AnyAsync(
                l => l.SubscriptionPeriodId == periodId && l.Invoice.Status != InvoiceStatus.Void,
                cancellationToken);

    /// <inheritdoc />
    public async Task<Invoice?> IssueForPeriodAsync(
        SubscriptionPeriod period, decimal amount, string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);

        // Nothing owed is not an invoice. A free plan renewing every month would otherwise generate a
        // stream of $0 documents that have to be read, filed and explained.
        if (amount <= 0m)
        {
            return null;
        }

        if (await HasBeenBilledAsync(period.Id, cancellationToken))
        {
            return null;
        }

        var subscription = period.Subscription
            ?? await dbContext.Set<Subscription>()
                .Include(s => s.Plan).ThenInclude(p => p.Prices)
                .FirstAsync(s => s.Id == period.SubscriptionId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var currency = CurrencyFor(subscription, period);
        var termsDays = await PaymentTermsDaysAsync(cancellationToken);

        var line = new InvoiceLine
        {
            Description = description,
            ServiceStart = period.StartDate,
            ServiceEnd = period.EndDate,
            Quantity = period.Quantity,
            // What one slot costs over this span, derived from what is actually being charged
            // rather than from the catalog - a prorated line's unit price is not the list price.
            UnitAmount = period.Quantity > 0 ? Round(amount / period.Quantity) : amount,
            Amount = amount,
            TaxAmount = 0m,
            SubscriptionPeriodId = period.Id
        };

        var invoice = new Invoice
        {
            // Assigned here, not left to EF's own Guid generation, because it's needed below as the
            // Stripe Tax calculation's reference - before this invoice (or its number) exists in the
            // database at all.
            Id = Guid.NewGuid(),
            TenantId = subscription.TenantId,
            Status = InvoiceStatus.Draft,
            Currency = currency,
            Subtotal = amount,
            // Zero unless StripeTaxService finds a billing address to calculate against below - see
            // Invoice.TaxTotal's remarks. The zero is honest rather than a placeholder: a tenant with
            // no billing address on file has not had their tax jurisdiction determined at all, not
            // "determined to owe nothing."
            TaxTotal = 0m,
            Total = amount,
            Lines = [line]
        };

        // A pending discount redemption, if this tenant has one waiting - see DiscountService.RedeemAsync
        // and Discount's own remarks for why redemption and application are two separate moments. Applied
        // (and the redemption spent) before tax is calculated: a discounted sale owes tax on what the
        // customer actually pays, never on the pre-discount list price.
        var pendingDiscount = await dbContext.Set<DiscountRedemption>()
            .Include(r => r.Discount)
            .Where(r => r.TenantId == subscription.TenantId && r.Status == DiscountRedemptionStatus.Pending)
            .OrderBy(r => r.RedeemedOn)
            .FirstOrDefaultAsync(cancellationToken);

        var taxableAmount = amount;

        if (pendingDiscount is not null)
        {
            var discountAmount = pendingDiscount.Discount.AmountType == DiscountAmountType.PercentOff
                ? Round(amount * pendingDiscount.Discount.AmountValue / 100m)
                : Math.Min(pendingDiscount.Discount.AmountValue, amount);

            taxableAmount = amount - discountAmount;
            invoice.DiscountTotal = discountAmount;
            invoice.DiscountCode = pendingDiscount.Discount.Code;
            invoice.Total = taxableAmount;

            pendingDiscount.Status = DiscountRedemptionStatus.Applied;
            pendingDiscount.InvoiceId = invoice.Id;
            pendingDiscount.DiscountAmount = discountAmount;
            pendingDiscount.AppliedOn = now;
        }

        // Sales tax, if this tenant has a billing address on file - see StripeTaxService's remarks for
        // why a failure here is left to propagate rather than swallowed: SubscriptionScheduleService's
        // own pass already treats "this period didn't get billed this time" as a normal, self-healing
        // state (see RenewAsync's remarks - the next hourly pass tries again), and issuing an invoice
        // with silently-wrong ($0) tax on it would be worse than a one-pass delay.
        var tax = await stripeTax.CalculateAsync(
            subscription.TenantId, taxableAmount, currency, invoice.Id.ToString(), cancellationToken);

        if (tax is not null)
        {
            invoice.TaxTotal = tax.TaxAmount;
            invoice.Total = taxableAmount + tax.TaxAmount;
            invoice.TaxTransactionId = tax.TransactionId;
            line.TaxAmount = tax.TaxAmount;
        }

        dbContext.Set<Invoice>().Add(invoice);

        // Finalise in the same unit of work. There is no drafting stage for a generated invoice - it is
        // complete the moment it is created - but the number is still taken at finalisation rather than
        // at creation, because that is the rule the sequence's gaplessness depends on.
        invoice.Number = await TakeNumberAsync(cancellationToken);
        invoice.Status = InvoiceStatus.Open;
        invoice.IssuedOn = now;
        invoice.DueOn = now.AddDays(termsDays);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Issued invoice {Number} to tenant {TenantId} for {Amount} {Currency}, due {DueOn}.",
            invoice.Number, invoice.TenantId, invoice.Total, invoice.Currency, invoice.DueOn);

        await NotifyIssuedAsync(invoice, cancellationToken);

        return invoice;
    }

    /// <summary>
    /// Queues the <see cref="EmailTemplateRegistry.Codes.InvoiceIssued"/> notice to the tenant's own
    /// <c>ContactEmail</c> - a no-op, not an error, for a tenant with none on file.
    /// </summary>
    private async Task NotifyIssuedAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.Id == invoice.TenantId)
            .Select(t => new { t.Name, t.ContactEmail })
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(tenant?.ContactEmail))
        {
            return;
        }

        await communicationPublisher.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.InvoiceIssued,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.OrganizationName] = tenant.Name,
                [EmailTemplateRegistry.Placeholders.InvoiceNumber] = invoice.Number ?? string.Empty,
                [EmailTemplateRegistry.Placeholders.AmountDue] = invoice.Total.ToString("C"),
                [EmailTemplateRegistry.Placeholders.DueDate] =
                    invoice.DueOn?.ToString("d MMM yyyy") ?? string.Empty
            },
            tenant.ContactEmail, userId: null, invoice.TenantId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Takes the next invoice number, holding the counter row until the caller's transaction ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SELECT ... FOR UPDATE</c> rather than a Postgres sequence, and deliberately so: <c>nextval</c>
    /// is non-transactional, so a finalisation that rolled back would leave a number consumed and a gap
    /// behind it - which is the one thing a gapless requirement exists to prevent. A locked counter row
    /// rolls back with everything else.
    /// </para>
    /// <para>
    /// The cost is that concurrent finalisations serialise here. That is the intended trade, and at any
    /// volume this system will see it is not measurable.
    /// </para>
    /// </remarks>
    private async Task<string> TakeNumberAsync(CancellationToken cancellationToken)
    {
        var scope = InvoiceNumberSequence.DefaultScope;

        var sequence = await dbContext.Set<InvoiceNumberSequence>()
            .FromSqlInterpolated($"""
                SELECT * FROM "InvoiceNumberSequence" WHERE "Scope" = {scope} FOR UPDATE
                """)
            .FirstOrDefaultAsync(cancellationToken);

        if (sequence is null)
        {
            // Seeded by the BillingTables migration, so its absence means someone removed it. Creating
            // one here would start numbering again from 1 and could duplicate an existing number, which
            // is worse than refusing.
            throw new InvalidOperationException(
                $"Invoice number sequence '{scope}' is missing. It is seeded by the BillingTables "
                + "migration and must exist before any invoice can be finalised.");
        }

        var value = sequence.NextValue;
        sequence.NextValue = value + 1;

        return sequence.Format(value);
    }

    /// <summary>
    /// The currency to bill in - the one on the plan's price for this period's term.
    /// </summary>
    /// <remarks>
    /// Read from the price rather than assumed, because <see cref="PlanPrice.Currency"/> is per price
    /// row precisely so a plan could eventually be sold in more than one. Falls back to USD when the
    /// plan has no price for the term, which is the same catalog defect the reports surface as
    /// "unpriced" - an invoice in an unknown currency is not an improvement on one in the default.
    /// </remarks>
    private static string CurrencyFor(Subscription subscription, SubscriptionPeriod period) =>
        subscription.Plan?.Prices.FirstOrDefault(p => p.TermMonths == period.TermMonths)?.Currency ?? "USD";

    private async Task<int> PaymentTermsDaysAsync(CancellationToken cancellationToken)
    {
        var raw = await dbContext.Set<PlatformSetting>()
            .Where(s => s.Key == PlatformSettingsRegistry.PaymentTermsDays)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        // A missing or nonsensical setting falls back rather than failing: an invoice with a sane due
        // date beats no invoice at all, and the fallback is the same value the setting is seeded with.
        return int.TryParse(raw, out var days) && days > 0
            ? days
            : PlatformSettingsRegistry.DefaultPaymentTermsDays;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
