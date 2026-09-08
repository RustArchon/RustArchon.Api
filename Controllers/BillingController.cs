// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Billing;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin settlement of invoices: recording payments, granting credit, and closing debts that
/// will never be paid.
/// </summary>
/// <remarks>
/// <para>
/// Gated by <c>ManageBilling</c>, its own permission rather than <c>ViewReports</c>. Reading what is
/// owed and deciding a debt will never be collected are different jobs, and only the second one moves
/// money in the books - somebody chasing invoices does not need the ability to write them off.
/// </para>
/// <para>
/// Every operation here is additive: nothing edits a finalised invoice. A payment writes a payment and
/// its allocations, a credit writes a credit note, and the invoice's balance falls out of the
/// arithmetic - see <see cref="IPaymentService"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/billing")]
[Authorize(Policy = "ManageBilling")]
public class BillingController(IPaymentService paymentService) : ControllerBase
{
    /// <summary>Invoices for the admin screen, newest first, optionally narrowed to one status.</summary>
    [HttpGet("invoices")]
    public async Task<ActionResult<IReadOnlyList<InvoiceDto>>> GetInvoices(
        [FromQuery] InvoiceStatus? status, [FromQuery] Guid? tenantId, CancellationToken cancellationToken) =>
        Ok(await paymentService.GetInvoicesAsync(status, tenantId, cancellationToken));

    /// <summary>
    /// Records money received against an invoice. Any excess spills onto the same Organization's other
    /// open invoices, oldest due first.
    /// </summary>
    [HttpPost("payments")]
    public async Task<ActionResult<InvoiceDto>> RecordPayment(
        [FromBody] RecordPaymentRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            await paymentService.RecordPaymentAsync(
                request.InvoiceId, request.Amount, request.Method, request.ReceivedOn,
                request.Reference, cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        return await SingleAsync(request.InvoiceId, cancellationToken);
    }

    /// <summary>Undoes a payment - a refund or a chargeback - reopening whatever it settled.</summary>
    [HttpPost("payments/{paymentId:guid}/reverse")]
    public async Task<IActionResult> ReversePayment(
        Guid paymentId, [FromQuery] PaymentStatus status, CancellationToken cancellationToken)
    {
        try
        {
            return await paymentService.ReversePaymentAsync(paymentId, status, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Grants value back against an invoice without money moving.</summary>
    [HttpPost("credit-notes")]
    public async Task<ActionResult<InvoiceDto>> IssueCreditNote(
        [FromBody] IssueCreditNoteRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            var note = await paymentService.IssueCreditNoteAsync(
                request.InvoiceId, request.Amount, request.Reason, cancellationToken);

            if (note is null)
            {
                return BadRequest("That invoice has nothing left to credit.");
            }
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        return await SingleAsync(request.InvoiceId, cancellationToken);
    }

    /// <summary>
    /// Marks an invoice as never having been owed. Refused once anything has been paid against it - see
    /// <see cref="IPaymentService.VoidInvoiceAsync"/>.
    /// </summary>
    [HttpPost("invoices/void")]
    public async Task<ActionResult<InvoiceDto>> VoidInvoice(
        [FromBody] CloseInvoiceRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            if (!await paymentService.VoidInvoiceAsync(request.InvoiceId, request.Reason, cancellationToken))
            {
                return BadRequest("That invoice can't be voided - it isn't open.");
            }
        }
        catch (InvalidOperationException ex)
        {
            // Already user-facing prose, so the Panel shows it directly - see ApiErrorMessage.
            return BadRequest(ex.Message);
        }

        return await SingleAsync(request.InvoiceId, cancellationToken);
    }

    /// <summary>Gives up on a debt that was genuinely owed, keeping it in the books as bad debt.</summary>
    [HttpPost("invoices/write-off")]
    public async Task<ActionResult<InvoiceDto>> WriteOffInvoice(
        [FromBody] CloseInvoiceRequestDto request, CancellationToken cancellationToken)
    {
        if (!await paymentService.WriteOffInvoiceAsync(request.InvoiceId, request.Reason, cancellationToken))
        {
            return BadRequest("That invoice can't be written off - it isn't open.");
        }

        return await SingleAsync(request.InvoiceId, cancellationToken);
    }

    /// <summary>
    /// Re-reads one invoice after a change, so the caller renders what the server now believes rather
    /// than what it predicted.
    /// </summary>
    private async Task<ActionResult<InvoiceDto>> SingleAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoices = await paymentService.GetInvoicesAsync(cancellationToken: cancellationToken);
        var invoice = invoices.FirstOrDefault(i => i.Id == invoiceId);

        return invoice is null ? NotFound() : Ok(invoice);
    }
}
