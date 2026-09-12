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

/// <inheritdoc cref="IPaymentService" />
public class PaymentService(
    ApiDbContext dbContext,
    ICommunicationPublisher communicationPublisher,
    TimeProvider timeProvider,
    ILogger<PaymentService> logger) : IPaymentService
{
    /// <inheritdoc />
    public async Task<Payment> RecordPaymentAsync(
        Guid invoiceId, decimal amount, PaymentMethod method, DateTimeOffset? receivedOn,
        string? reference, CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment must be for a positive amount.");
        }

        var target = await dbContext.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} does not exist.");

        var now = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var payment = new Payment
        {
            TenantId = target.TenantId,
            Amount = amount,
            Currency = target.Currency,
            Status = PaymentStatus.Succeeded,
            Method = method,
            // Trusted from the caller: a bank transfer entered on Friday may have landed on Tuesday, and
            // the aging of everything it settles depends on the real date rather than the typing date.
            ReceivedOn = receivedOn ?? now,
            Reference = reference
        };
        dbContext.Set<Payment>().Add(payment);
        await dbContext.SaveChangesAsync(cancellationToken);

        // The named invoice first, then the rest of this Organization's open ones oldest-due first. Any
        // remainder stays unallocated rather than being refused - see IPaymentService.
        var queue = new List<Invoice> { target };
        queue.AddRange(await dbContext.Set<Invoice>()
            .Where(i => i.TenantId == target.TenantId && i.Status == InvoiceStatus.Open && i.Id != target.Id)
            .OrderBy(i => i.DueOn)
            .ToListAsync(cancellationToken));

        var remaining = amount;
        foreach (var invoice in queue)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var owed = invoice.AmountOutstanding;
            if (owed <= 0m)
            {
                continue;
            }

            var applied = Math.Min(remaining, owed);

            dbContext.Set<PaymentAllocation>().Add(new PaymentAllocation
            {
                PaymentId = payment.Id,
                InvoiceId = invoice.Id,
                Amount = applied,
                AllocatedOn = now
            });

            invoice.AmountPaid += applied;
            Settle(invoice);
            remaining -= applied;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Recorded {Method} payment of {Amount} {Currency} for tenant {TenantId}; {Unallocated} left unallocated.",
            method, amount, payment.Currency, payment.TenantId, remaining);

        await NotifyReceivedAsync(payment, cancellationToken);

        return payment;
    }

    /// <summary>
    /// Queues the <see cref="EmailTemplateRegistry.Codes.PaymentReceived"/> receipt to the tenant's
    /// own <c>ContactEmail</c> - a no-op, not an error, for a tenant with none on file.
    /// </summary>
    private async Task NotifyReceivedAsync(Payment payment, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.Id == payment.TenantId)
            .Select(t => new { t.Name, t.ContactEmail })
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(tenant?.ContactEmail))
        {
            return;
        }

        await communicationPublisher.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.PaymentReceived,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.OrganizationName] = tenant.Name,
                [EmailTemplateRegistry.Placeholders.AmountPaid] = payment.Amount.ToString("C"),
                [EmailTemplateRegistry.Placeholders.ReceivedDate] = payment.ReceivedOn.ToString("d MMM yyyy")
            },
            tenant.ContactEmail, userId: null, payment.TenantId, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ReversePaymentAsync(
        Guid paymentId, PaymentStatus status, CancellationToken cancellationToken = default)
    {
        if (status is not (PaymentStatus.Refunded or PaymentStatus.Disputed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), "A payment is reversed by refunding or disputing it, nothing else.");
        }

        var payment = await dbContext.Set<Payment>()
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment is null || payment.Status != PaymentStatus.Succeeded)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var live = payment.Allocations.Where(a => a.ReversedOn is null).ToList();
        var invoiceIds = live.Select(a => a.InvoiceId).Distinct().ToList();

        var invoices = await dbContext.Set<Invoice>()
            .Where(i => invoiceIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var allocation in live)
        {
            // Stamped rather than deleted: an invoice that reopens with no record of why is the state
            // this whole model exists to avoid.
            allocation.ReversedOn = now;

            if (invoices.TryGetValue(allocation.InvoiceId, out var invoice))
            {
                invoice.AmountPaid -= allocation.Amount;
                Settle(invoice);
            }
        }

        payment.Status = status;

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Payment {PaymentId} marked {Status}; {Count} allocation(s) reversed.",
            paymentId, status, live.Count);

        return true;
    }

    /// <inheritdoc />
    public async Task<CreditNote?> IssueCreditNoteAsync(
        Guid invoiceId, decimal amount, string reason, CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A credit note must be for a positive amount.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A credit note needs a reason.", nameof(reason));
        }

        var invoice = await dbContext.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();

        // Capped at what's still owed. Crediting past zero would make an invoice show a negative
        // balance, which reads as the business owing the customer - a different thing entirely, and one
        // that belongs on the account rather than on a document that has been settled.
        var applied = Math.Min(amount, invoice.AmountOutstanding);
        if (applied <= 0m)
        {
            return null;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var note = new CreditNote
        {
            TenantId = invoice.TenantId,
            Amount = applied,
            Currency = invoice.Currency,
            Reason = reason.Trim(),
            IssuedOn = now,
            AppliedToInvoiceId = invoice.Id
        };
        dbContext.Set<CreditNote>().Add(note);

        invoice.AmountCredited += applied;
        Settle(invoice);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Issued credit note of {Amount} against invoice {Number}: {Reason}",
            applied, invoice.Number, note.Reason);

        return note;
    }

    /// <inheritdoc />
    public async Task<bool> VoidInvoiceAsync(
        Guid invoiceId, string? reason, CancellationToken cancellationToken = default)
    {
        var invoice = await dbContext.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null || invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Draft))
        {
            return false;
        }

        // Voiding says the invoice should never have existed. Money already applied to it would then
        // have nothing to explain it, so the payment has to be reversed first - refusing here is what
        // stops a settled document being erased out from under its own payment record.
        if (invoice.AmountPaid > 0m)
        {
            throw new InvalidOperationException(
                $"Invoice {invoice.Number} has {invoice.AmountPaid:C} paid against it. "
                + "Refund or dispute that payment first, then void the invoice.");
        }

        var now = timeProvider.GetUtcNow();
        invoice.Status = InvoiceStatus.Void;
        invoice.VoidedOn = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Voided invoice {Number}: {Reason}", invoice.Number, reason ?? "no reason given");
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> WriteOffInvoiceAsync(
        Guid invoiceId, string? reason, CancellationToken cancellationToken = default)
    {
        var invoice = await dbContext.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null || invoice.Status != InvoiceStatus.Open)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        invoice.Status = InvoiceStatus.Uncollectible;
        invoice.WrittenOffOn = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Wrote off invoice {Number} with {Outstanding} outstanding: {Reason}",
            invoice.Number, invoice.AmountOutstanding, reason ?? "no reason given");

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvoiceDto>> GetInvoicesAsync(
        InvoiceStatus? status = null, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var invoices = await dbContext.Set<Invoice>()
            .Include(i => i.Tenant)
            .Include(i => i.Lines)
            .Where(i => i.Tenant.DeletedOn == null
                        && (status == null || i.Status == status)
                        && (tenantId == null || i.TenantId == tenantId))
            .OrderByDescending(i => i.IssuedOn)
            .ThenByDescending(i => i.Number)
            .Take(500)
            .ToListAsync(cancellationToken);

        var ids = invoices.Select(i => i.Id).ToList();

        var allocations = await dbContext.Set<PaymentAllocation>()
            .Include(a => a.Payment)
            .Where(a => ids.Contains(a.InvoiceId))
            .ToListAsync(cancellationToken);

        var credits = await dbContext.Set<CreditNote>()
            .Where(c => c.AppliedToInvoiceId != null && ids.Contains(c.AppliedToInvoiceId.Value))
            .ToListAsync(cancellationToken);

        return invoices.Select(i => new InvoiceDto
        {
            Id = i.Id,
            TenantId = i.TenantId,
            OrganizationName = i.Tenant.Name,
            ContactEmail = i.Tenant.ContactEmail,
            Number = i.Number,
            Status = i.Status,
            Currency = i.Currency,
            IssuedOn = i.IssuedOn,
            DueOn = i.DueOn,
            Subtotal = i.Subtotal,
            TaxTotal = i.TaxTotal,
            Total = i.Total,
            AmountPaid = i.AmountPaid,
            AmountCredited = i.AmountCredited,
            Outstanding = i.AmountOutstanding,
            DaysOverdue = i.DueOn is { } due && i.Status == InvoiceStatus.Open
                ? (int)Math.Floor((now - due).TotalDays)
                : 0,
            VoidedOn = i.VoidedOn,
            WrittenOffOn = i.WrittenOffOn,
            Lines = i.Lines.Select(l => new InvoiceLineDto
            {
                Description = l.Description,
                ServiceStart = l.ServiceStart,
                ServiceEnd = l.ServiceEnd,
                Quantity = l.Quantity,
                UnitAmount = l.UnitAmount,
                Amount = l.Amount,
                TaxAmount = l.TaxAmount
            }).ToList(),
            // Payments and credits shown together: from the invoice's side they answer the same
            // question, which is why the balance is what it is.
            Settlements = allocations
                .Where(a => a.InvoiceId == i.Id)
                .Select(a => new InvoiceSettlementDto
                {
                    Id = a.PaymentId,
                    Kind = "Payment",
                    Amount = a.Amount,
                    AppliedOn = a.Payment.ReceivedOn,
                    Method = a.Payment.Method,
                    Reference = a.Payment.Reference,
                    ReversedOn = a.ReversedOn
                })
                .Concat(credits
                    .Where(c => c.AppliedToInvoiceId == i.Id)
                    .Select(c => new InvoiceSettlementDto
                    {
                        Id = c.Id,
                        Kind = "Credit note",
                        Amount = c.Amount,
                        AppliedOn = c.IssuedOn,
                        Reference = c.Reason
                    }))
                .OrderBy(s => s.AppliedOn)
                .ToList()
        }).ToList();
    }

    /// <summary>
    /// Moves an invoice's status to match its balance, in whichever direction the balance just went.
    /// </summary>
    /// <remarks>
    /// The single place status follows money, so a reversal reopens an invoice by exactly the same rule
    /// that closed it. Only ever moves between Open and Paid - a voided or written-off invoice is
    /// finished, and a late payment against one is a decision for a person rather than something to
    /// silently undo.
    /// </remarks>
    private static void Settle(Invoice invoice)
    {
        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Paid))
        {
            return;
        }

        invoice.Status = invoice.AmountOutstanding <= 0m ? InvoiceStatus.Paid : InvoiceStatus.Open;
    }
}
