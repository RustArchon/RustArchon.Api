// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <inheritdoc cref="IInvoiceService" />
public class InvoiceService(
    ApiDbContext dbContext,
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

        var invoice = new Invoice
        {
            TenantId = subscription.TenantId,
            Status = InvoiceStatus.Draft,
            Currency = currency,
            Subtotal = amount,
            // Modelled but not calculated - see Invoice.TaxTotal. A provider fills this in later; today
            // every invoice is net-only, and the zero is honest rather than a placeholder for an
            // unknown figure folded into the total.
            TaxTotal = 0m,
            Total = amount,
            Lines =
            [
                new InvoiceLine
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
                }
            ]
        };

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

        return invoice;
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
