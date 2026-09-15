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
    IOrganizationLifecycleService organizationLifecycle,
    IStripeRefundService stripeRefund,
    IStripeTaxService stripeTax,
    TimeProvider timeProvider,
    ILogger<PaymentService> logger) : IPaymentService
{
    /// <inheritdoc />
    /// <remarks>
    /// <strong>Idempotent when <paramref name="providerPaymentId"/> is set.</strong> A payment gateway's
    /// webhook is delivered at-least-once, so the same confirmed payment can arrive here more than
    /// once - this checks for an existing <see cref="Payment"/> with the same
    /// <see cref="Payment.ProviderPaymentId"/> first and returns it unchanged rather than allocating the
    /// same money twice. A manually-entered payment (<paramref name="providerPaymentId"/> null) has no
    /// such check, and doesn't need one - nothing redelivers a person's own click.
    /// </remarks>
    public async Task<Payment> RecordPaymentAsync(
        Guid invoiceId, decimal amount, PaymentMethod method, DateTimeOffset? receivedOn,
        string? reference, string? providerPaymentId = null, CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment must be for a positive amount.");
        }

        if (!string.IsNullOrEmpty(providerPaymentId))
        {
            var existing = await dbContext.Set<Payment>()
                .Include(p => p.Allocations)
                .FirstOrDefaultAsync(p => p.ProviderPaymentId == providerPaymentId, cancellationToken);

            if (existing is not null)
            {
                logger.LogInformation(
                    "Payment for provider id {ProviderPaymentId} already recorded as {PaymentId}; skipping.",
                    providerPaymentId, existing.Id);
                return existing;
            }
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
            Reference = reference,
            ProviderPaymentId = providerPaymentId
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
        await ReactivateIfClearAsync(target.TenantId, cancellationToken);

        return payment;
    }

    /// <inheritdoc />
    public async Task<Payment?> RecordFailedPaymentAsync(
        Guid invoiceId, decimal amount, PaymentMethod method, string providerPaymentId, string providerEventId,
        string? failureCode, string? failureMessage, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Set<Payment>()
            .FirstOrDefaultAsync(p => p.ProviderEventId == providerEventId, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Failed payment for event {ProviderEventId} already recorded as {PaymentId}; skipping.",
                providerEventId, existing.Id);
            return null;
        }

        var invoice = await dbContext.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null)
        {
            logger.LogWarning(
                "A Stripe payment failure named invoice {InvoiceId}, which doesn't exist - not recorded.",
                invoiceId);
            return null;
        }

        var payment = new Payment
        {
            TenantId = invoice.TenantId,
            Amount = amount,
            Currency = invoice.Currency,
            Status = PaymentStatus.Failed,
            Method = method,
            ReceivedOn = timeProvider.GetUtcNow(),
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            ProviderPaymentId = providerPaymentId,
            ProviderEventId = providerEventId
        };
        dbContext.Set<Payment>().Add(payment);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Recorded failed {Method} payment of {Amount} {Currency} for tenant {TenantId}: {FailureCode} - {FailureMessage}",
            method, amount, payment.Currency, payment.TenantId, failureCode, failureMessage);

        return payment;
    }

    /// <summary>
    /// Moves the tenant back to <see cref="SubscriptionStatus.Active"/> the moment every overdue
    /// invoice is actually cleared - <see cref="DunningService"/>'s escalation counterpart, but
    /// immediate rather than waiting out that sweep's next hourly pass. A paying customer's servers
    /// should come back the moment they've paid, not up to an hour later.
    /// </summary>
    /// <remarks>
    /// A no-op unless the subscription is currently <see cref="SubscriptionStatus.PastDue"/> or
    /// <see cref="SubscriptionStatus.Suspended"/> - checked first here (rather than relying solely on
    /// <see cref="IOrganizationLifecycleService.SetStatusAsync"/>'s own no-op-if-already-there guard) so
    /// the overwhelmingly common case, an on-time payment against an already-Active tenant, costs one
    /// query instead of two.
    /// </remarks>
    private async Task ReactivateIfClearAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var status = await dbContext.Set<Subscription>()
            .Where(s => s.TenantId == tenantId && s.EndDate == null)
            .Select(s => (SubscriptionStatus?)s.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is not (SubscriptionStatus.PastDue or SubscriptionStatus.Suspended))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var stillOverdue = await dbContext.Set<Invoice>()
            .AnyAsync(i => i.TenantId == tenantId && i.Status == InvoiceStatus.Open
                && i.DueOn != null && i.DueOn <= now, cancellationToken);

        if (stillOverdue)
        {
            return;
        }

        await organizationLifecycle.SetStatusAsync(
            tenantId, SubscriptionStatus.Active, "Payment received.", cancellationToken);
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
    /// <remarks>
    /// <strong>Stripe first, books second.</strong> When this payment was collected through Stripe, the
    /// actual refund call happens before anything here is touched - if Stripe declines it or the call
    /// fails outright, this method throws (or Stripe's own exception propagates) and not one row has
    /// changed. The alternative - reversing the books first and refunding Stripe after - risks a state
    /// where RustArchon thinks a customer got their money back and Stripe never actually sent it.
    /// </remarks>
    public async Task<bool> ReversePaymentAsync(
        Guid paymentId, PaymentStatus status, decimal? amount = null, CancellationToken cancellationToken = default)
    {
        if (status is not (PaymentStatus.Refunded or PaymentStatus.Disputed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), "A payment is reversed by refunding or disputing it, nothing else.");
        }

        if (amount is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A reversal must be for a positive amount.");
        }

        var payment = await dbContext.Set<Payment>()
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment is null || payment.Status != PaymentStatus.Succeeded)
        {
            return false;
        }

        // Oldest allocation first - the same ordering RecordPaymentAsync's own overpayment spillover
        // already uses, so which invoice gets a partial reversal applied to it first is at least
        // predictable rather than arbitrary.
        var live = payment.Allocations
            .Where(a => a.ReversedAmount < a.Amount)
            .OrderBy(a => a.AllocatedOn)
            .ToList();

        var totalLive = live.Sum(a => a.Amount - a.ReversedAmount);
        var requestedAmount = amount ?? totalLive;

        if (requestedAmount > totalLive)
        {
            return false;
        }

        var invoiceIds = live.Select(a => a.InvoiceId).Distinct().ToList();
        var invoices = await dbContext.Set<Invoice>()
            .Where(i => invoiceIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        // Stripe first - see this method's own remarks. Nothing below runs at all if this throws.
        if (!string.IsNullOrEmpty(payment.ProviderPaymentId) && payment.Method == PaymentMethod.Card)
        {
            await stripeRefund.RefundAsync(payment.ProviderPaymentId, requestedAmount, cancellationToken);
        }

        await ApplyReversalAsync(payment, live, invoices, requestedAmount, status, cancellationToken);
        return true;
    }

    /// <summary>
    /// The bookkeeping shared by a refund/dispute reversal <see cref="ReversePaymentAsync"/> initiates and
    /// a chargeback <see cref="RecordDisputeAsync"/> is told about after the fact: unwind allocations
    /// oldest-first, reopen whatever invoices they touched, and reverse each invoice's Stripe Tax
    /// transaction by the matching share. Never calls Stripe's Refund API itself - that only ever
    /// belongs before this, in <see cref="ReversePaymentAsync"/>, and never happens at all for a
    /// chargeback (Stripe already took the money; there is nothing left to refund).
    /// </summary>
    private async Task ApplyReversalAsync(
        Payment payment, IReadOnlyList<PaymentAllocation> live, IReadOnlyDictionary<Guid, Invoice> invoices,
        decimal requestedAmount, PaymentStatus status, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var remaining = requestedAmount;
        var touched = 0;

        foreach (var allocation in live)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var availableOnThisAllocation = allocation.Amount - allocation.ReversedAmount;
            var reverseNow = Math.Min(remaining, availableOnThisAllocation);

            allocation.ReversedAmount += reverseNow;
            if (allocation.ReversedAmount >= allocation.Amount)
            {
                allocation.ReversedOn = now;
            }

            if (invoices.TryGetValue(allocation.InvoiceId, out var invoice))
            {
                invoice.AmountPaid -= reverseNow;
                Settle(invoice);

                if (!string.IsNullOrEmpty(invoice.TaxTransactionId))
                {
                    // Stripe determines the tax-vs-base split of reverseNow itself - see
                    // IStripeTaxService.ReverseAsync's own remarks. A unique reference every time,
                    // since Stripe refuses a repeated one - the allocation's own id plus how much of
                    // its running reversal total this call reaches is enough to make each reversal
                    // event distinct even across several partial refunds against the same allocation.
                    await stripeTax.ReverseAsync(
                        invoice.TaxTransactionId, reverseNow,
                        referenceId: $"{allocation.Id}-reversal-{allocation.ReversedAmount:F2}",
                        cancellationToken);
                }
            }

            remaining -= reverseNow;
            touched++;
        }

        // Only once every dollar this payment ever settled has actually been reversed - a partial
        // refund leaves the payment Succeeded, same as Stripe's own Charge.status does, since it is
        // still fundamentally the payment that happened, just partly given back.
        var fullyReversed = payment.Allocations.Sum(a => a.ReversedAmount) >= payment.Amount;
        if (fullyReversed)
        {
            payment.Status = status;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Payment {PaymentId}: reversed {Amount} across {Count} allocation(s){Fully}.",
            payment.Id, requestedAmount, touched, fullyReversed ? $" (now fully {status})" : " (partial)");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>The money has already moved by the time this is ever called.</strong> Unlike
    /// <see cref="ReversePaymentAsync"/>, there is no Stripe API call here at all - a chargeback means the
    /// card network already pulled the funds out of the Stripe balance before this webhook even arrives,
    /// so the books simply have to catch up to what already happened, using the same
    /// <see cref="ApplyReversalAsync"/> bookkeeping a refund uses.
    /// </remarks>
    public async Task<Payment?> RecordDisputeAsync(
        string providerPaymentId, string disputeId, string? reason, DateTimeOffset? dueBy,
        CancellationToken cancellationToken = default)
    {
        var payment = await dbContext.Set<Payment>()
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.ProviderPaymentId == providerPaymentId, cancellationToken);

        if (payment is null)
        {
            logger.LogWarning(
                "A Stripe dispute named PaymentIntent {ProviderPaymentId}, which matches no recorded payment - not recorded.",
                providerPaymentId);
            return null;
        }

        if (payment.DisputeId == disputeId)
        {
            logger.LogInformation(
                "Dispute {DisputeId} for payment {PaymentId} already recorded; skipping.", disputeId, payment.Id);
            return payment;
        }

        payment.DisputeId = disputeId;
        payment.DisputeReason = reason;
        payment.DisputeDueBy = dueBy;

        if (payment.Status == PaymentStatus.Succeeded)
        {
            var live = payment.Allocations
                .Where(a => a.ReversedAmount < a.Amount)
                .OrderBy(a => a.AllocatedOn)
                .ToList();
            var totalLive = live.Sum(a => a.Amount - a.ReversedAmount);

            if (totalLive > 0m)
            {
                var invoiceIds = live.Select(a => a.InvoiceId).Distinct().ToList();
                var invoices = await dbContext.Set<Invoice>()
                    .Where(i => invoiceIds.Contains(i.Id))
                    .ToDictionaryAsync(i => i.Id, cancellationToken);

                await ApplyReversalAsync(payment, live, invoices, totalLive, PaymentStatus.Disputed, cancellationToken);
            }
            else
            {
                // Nothing left live to reverse (already fully refunded some other way) - still a
                // dispute worth recording, just with no bookkeeping left for it to do.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        else
        {
            // Already Refunded/Disputed/Failed by the time this arrived (an admin's own manual action
            // beat the webhook here, or this is a second dispute against an already-disputed payment) -
            // nothing to reverse again, just persist the dispute's own metadata.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogWarning(
            "Payment {PaymentId} disputed via Stripe chargeback {DisputeId}: {Reason}, evidence due {DueBy}.",
            payment.Id, disputeId, reason, dueBy);

        return payment;
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
                    ReversedOn = a.ReversedOn,
                    ReversedAmount = a.ReversedAmount,
                    Status = a.Payment.Status,
                    DisputeId = a.Payment.DisputeId
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
