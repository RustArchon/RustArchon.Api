// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Billing;

/// <summary>
/// Assembles, and submits to Stripe, the evidence packet for a disputed payment.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Built entirely from data this codebase already collects.</strong> There is no IP address or
/// page-visit tracking in RustArchon (a deliberate gap - see the design discussion this feature came out
/// of), so this doesn't invent any: instead it draws on what genuinely exists and genuinely proves
/// something - the invoice's own service period, real RCON connection activity on the tenant's servers
/// during that period (the service was actually running and administered), and delivery/open tracking on
/// every email sent about the payment (the customer was kept informed, not left in the dark).
/// </para>
/// <para>
/// <strong>Only ever reads a payment that is already <see cref="PaymentStatus.Disputed"/> with a
/// <see cref="Payment.DisputeId"/> on it.</strong> There is nothing to defend before Stripe has actually
/// reported a chargeback - see <see cref="IPaymentService.RecordDisputeAsync"/>, the only writer of that
/// state.
/// </para>
/// </remarks>
public interface IChargebackEvidenceService
{
    /// <returns><c>null</c> if the payment doesn't exist or isn't disputed.</returns>
    Task<ChargebackEvidenceDto?> GetEvidenceAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assembles the same evidence <see cref="GetEvidenceAsync"/> would return and submits it to Stripe
    /// as the dispute's formal response (<c>Submit = true</c> - see <c>IStripeDisputeService</c>'s own
    /// remarks for why this is a one-shot action, not a draft save).
    /// </summary>
    /// <returns><c>false</c> if the payment doesn't exist or isn't disputed.</returns>
    Task<bool> SubmitEvidenceAsync(Guid paymentId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IChargebackEvidenceService" />
public class ChargebackEvidenceService(
    ApiDbContext dbContext, IStripeDisputeService stripeDispute, TimeProvider timeProvider,
    ILogger<ChargebackEvidenceService> logger) : IChargebackEvidenceService
{
    /// <inheritdoc />
    public async Task<ChargebackEvidenceDto?> GetEvidenceAsync(
        Guid paymentId, CancellationToken cancellationToken = default)
    {
        var payment = await dbContext.Set<Payment>()
            .Include(p => p.Tenant)
            .Include(p => p.Allocations).ThenInclude(a => a.Invoice).ThenInclude(i => i.Lines)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment is null || string.IsNullOrEmpty(payment.DisputeId))
        {
            return null;
        }

        var billingAddress = await dbContext.Set<TenantBillingAddress>()
            .FirstOrDefaultAsync(b => b.TenantId == payment.TenantId, cancellationToken);

        var priorSuccessfulPayments = await dbContext.Set<Payment>()
            .CountAsync(p => p.TenantId == payment.TenantId
                && p.Status == PaymentStatus.Succeeded
                && p.ReceivedOn < payment.ReceivedOn, cancellationToken);

        var invoices = payment.Allocations
            .Select(a => a.Invoice)
            .DistinctBy(i => i.Id)
            .OrderBy(i => i.IssuedOn)
            .ToList();

        var invoiceDtos = invoices.Select(i => new ChargebackEvidenceInvoiceDto
        {
            Number = i.Number ?? string.Empty,
            IssuedOn = i.IssuedOn,
            DueOn = i.DueOn,
            Total = i.Total,
            ServiceStart = i.Lines.Count > 0 ? i.Lines.Min(l => l.ServiceStart) : null,
            ServiceEnd = i.Lines.Count > 0 ? i.Lines.Max(l => l.ServiceEnd) : null,
            LineDescriptions = i.Lines.Select(l => l.Description).ToList()
        }).ToList();

        // The window server activity is checked against - every invoice's service period this payment
        // covers, or a week either side of when the payment was received if no line carries one (a
        // payment with nothing allocated yet, or lines with no dates for some other reason).
        var periodStart = invoiceDtos.Select(i => i.ServiceStart).Where(d => d is not null).Select(d => d!.Value)
            .DefaultIfEmpty(payment.ReceivedOn.AddDays(-7)).Min();
        var periodEnd = invoiceDtos.Select(i => i.ServiceEnd).Where(d => d is not null).Select(d => d!.Value)
            .DefaultIfEmpty(payment.ReceivedOn.AddDays(7)).Max();

        var servers = await dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .Where(s => s.TenantId == payment.TenantId)
            .ToListAsync(cancellationToken);
        var serverIds = servers.Select(s => s.Id).ToList();

        var activity = await dbContext.Set<ConnectionLogEntry>()
            .AcrossAllTenants()
            .Where(e => serverIds.Contains(e.RustServerId) && e.OccurredAtUtc >= periodStart && e.OccurredAtUtc <= periodEnd)
            .ToListAsync(cancellationToken);
        var activityByServer = activity.ToLookup(e => e.RustServerId);

        var serverDtos = servers.Select(s =>
        {
            var events = activityByServer[s.Id].ToList();
            return new ChargebackEvidenceServerDto
            {
                Name = s.Name,
                CreatedOn = s.CreatedOn,
                ConnectionEventCount = events.Count,
                FirstActivityInPeriod = events.Count > 0 ? events.Min(e => e.OccurredAtUtc) : null,
                LastActivityInPeriod = events.Count > 0 ? events.Max(e => e.OccurredAtUtc) : null
            };
        }).ToList();

        // Every communication addressed to this Organization from just before the earliest invoice was
        // issued through just after the payment was received - wide enough to catch the invoice-issued
        // and payment-received receipts without dumping the tenant's entire mail history.
        var commWindowStart = (invoiceDtos.Count > 0 ? invoiceDtos.Min(i => i.IssuedOn) : null) ?? payment.ReceivedOn.AddDays(-14);
        var commWindowEnd = payment.ReceivedOn.AddDays(7);

        var communications = await dbContext.Set<Communication>()
            .AcrossAllTenants()
            .Where(c => c.TenantId == payment.TenantId && c.QueuedOn >= commWindowStart && c.QueuedOn <= commWindowEnd)
            .OrderBy(c => c.QueuedOn)
            .Select(c => new ChargebackEvidenceCommunicationDto
            {
                Subject = c.Subject,
                SentOn = c.SentOn,
                ViewedOn = c.ViewedOn
            })
            .ToListAsync(cancellationToken);

        var billingAddressText = FormatAddress(billingAddress);

        var dto = new ChargebackEvidenceDto
        {
            PaymentId = payment.Id,
            DisputeId = payment.DisputeId,
            DisputeReason = payment.DisputeReason,
            DisputeDueBy = payment.DisputeDueBy,
            DisputeEvidenceSubmittedOn = payment.DisputeEvidenceSubmittedOn,
            Amount = payment.Amount,
            Currency = payment.Currency,
            ProviderPaymentId = payment.ProviderPaymentId,
            ReceivedOn = payment.ReceivedOn,
            TenantId = payment.TenantId,
            OrganizationName = payment.Tenant.Name,
            ContactEmail = payment.Tenant.ContactEmail,
            BillingAddress = billingAddressText,
            CustomerSinceOn = payment.Tenant.CreatedOn,
            PriorSuccessfulPaymentCount = priorSuccessfulPayments,
            Invoices = invoiceDtos,
            Servers = serverDtos,
            Communications = communications
        };

        dto.SummaryText = BuildSummary(dto);
        return dto;
    }

    /// <inheritdoc />
    public async Task<bool> SubmitEvidenceAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        var evidence = await GetEvidenceAsync(paymentId, cancellationToken);
        if (evidence is null)
        {
            return false;
        }

        var earliestServiceDate = evidence.Invoices
            .Select(i => i.ServiceStart)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .OrderBy(d => d)
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();

        await stripeDispute.SubmitEvidenceAsync(
            evidence.DisputeId!,
            new DisputeEvidenceInput(
                CustomerEmailAddress: evidence.ContactEmail,
                CustomerName: evidence.OrganizationName,
                ProductDescription: "Rust game server hosting and management subscription (RustArchon).",
                ServiceDate: earliestServiceDate?.ToString("yyyy-MM-dd"),
                BillingAddress: evidence.BillingAddress,
                UncategorizedText: evidence.SummaryText),
            cancellationToken);

        var payment = await dbContext.Set<Payment>().FirstAsync(p => p.Id == paymentId, cancellationToken);

        // Never overwritten by a later submission - see Payment.DisputeEvidenceSubmittedOn's own remarks.
        payment.DisputeEvidenceSubmittedOn ??= timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Submitted chargeback evidence for payment {PaymentId} (dispute {DisputeId}).", paymentId, evidence.DisputeId);
        return true;
    }

    private static string? FormatAddress(TenantBillingAddress? address)
    {
        if (address is null || string.IsNullOrEmpty(address.Country))
        {
            return null;
        }

        var parts = new[] { address.Line1, address.Line2, address.City, address.State, address.PostalCode, address.Country }
            .Where(p => !string.IsNullOrWhiteSpace(p));

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The narrative Stripe's <c>uncategorized_text</c> evidence field carries - the one field with no
    /// structured equivalent for "here is proof the service ran" or "here is proof they opened our
    /// emails", which is most of what this codebase can actually offer.
    /// </summary>
    private static string BuildSummary(ChargebackEvidenceDto dto)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $"{dto.OrganizationName} has been a customer since {dto.CustomerSinceOn:d MMM yyyy} " +
            $"({dto.PriorSuccessfulPaymentCount} prior successful payment(s) before this one).");

        foreach (var invoice in dto.Invoices)
        {
            var period = invoice.ServiceStart is { } start && invoice.ServiceEnd is { } end
                ? $"service period {start:d MMM yyyy} - {end:d MMM yyyy}"
                : "no service period recorded";
            sb.AppendLine(
                $"Invoice {invoice.Number}: {invoice.Total:C} ({period}). " +
                $"{string.Join("; ", invoice.LineDescriptions)}");
        }

        foreach (var server in dto.Servers)
        {
            sb.AppendLine(server.ConnectionEventCount > 0
                ? $"Server '{server.Name}' (added {server.CreatedOn:d MMM yyyy}) was actively connected to " +
                  $"and administered {server.ConnectionEventCount} time(s) during the service period, " +
                  $"from {server.FirstActivityInPeriod:d MMM yyyy} to {server.LastActivityInPeriod:d MMM yyyy}."
                : $"Server '{server.Name}' (added {server.CreatedOn:d MMM yyyy}) shows no recorded activity in the service period.");
        }

        foreach (var comm in dto.Communications)
        {
            sb.AppendLine(comm.ViewedOn is { } viewed
                ? $"Email \"{comm.Subject}\" was sent {comm.SentOn:d MMM yyyy} and opened by the customer {viewed:d MMM yyyy}."
                : $"Email \"{comm.Subject}\" was sent {comm.SentOn:d MMM yyyy}.");
        }

        return sb.ToString().TrimEnd();
    }
}
