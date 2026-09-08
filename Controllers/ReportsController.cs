// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Reporting;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-wide operational reports. One endpoint per report, each returning a complete
/// <see cref="ReportResult{TRow}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Gated by <c>ViewReports</c> rather than by any of the three existing Site Admin policies. Reading
/// what the business is owed is a different job from editing the plan catalog or the platform settings,
/// and someone doing the first shouldn't need the rights to do the other two. Same mechanism as
/// <see cref="PlansController"/> and friends: a claim on the global Site Admin role, not
/// <c>[EntityAuthorize]</c>, because none of this is tenant-scoped.
/// </para>
/// <para>
/// <strong>These read across every Organization.</strong> That is the point of them, and it is why the
/// policy is the only thing standing in front of them - there is no tenant context to fall back on if
/// it were ever removed. The one report a customer sees - their own billing history - is deliberately
/// not here; it lives on <see cref="SubscriptionController"/>, scoped to the caller's own tenant.
/// </para>
/// <para>
/// Date windows are stated by the caller rather than defaulted here. The window is the whole point of
/// a report, so the Panel puts its default in the URL on first load - which keeps the range someone is
/// looking at visible and shareable rather than implied by a server-side default that could change.
/// </para>
/// </remarks>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = "ViewReports")]
public class ReportsController(IReportingService reportingService) : ControllerBase
{
    /// <summary>Organizations renewing between <paramref name="from"/> and <paramref name="to"/>.</summary>
    [HttpGet("upcoming-renewals")]
    public async Task<ActionResult<ReportResult<UpcomingRenewalRowDto>>> GetUpcomingRenewals(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? planId,
        CancellationToken cancellationToken)
    {
        if (InvalidRange(from, to, out var error))
        {
            return error;
        }

        return Ok(await reportingService.GetUpcomingRenewalsAsync(from, to, planId, cancellationToken));
    }

    /// <summary>Organizations created between <paramref name="from"/> and <paramref name="to"/>.</summary>
    [HttpGet("new-signups")]
    public async Task<ActionResult<ReportResult<NewSignupRowDto>>> GetNewSignups(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken cancellationToken)
    {
        if (InvalidRange(from, to, out var error))
        {
            return error;
        }

        return Ok(await reportingService.GetNewSignupsAsync(from, to, cancellationToken));
    }

    /// <summary>
    /// Every live subscription. Takes no date window on purpose - this is a snapshot of what is true
    /// now, and a range would only invite the question of what a subscription "in" a window means.
    /// </summary>
    [HttpGet("subscriptions")]
    public async Task<ActionResult<ReportResult<SubscriptionRegisterRowDto>>> GetSubscriptions(
        [FromQuery] Guid? planId, CancellationToken cancellationToken) =>
        Ok(await reportingService.GetSubscriptionsAsync(planId, cancellationToken));

    /// <summary>Plan moves between <paramref name="from"/> and <paramref name="to"/>.</summary>
    [HttpGet("plan-changes")]
    public async Task<ActionResult<ReportResult<PlanChangeRowDto>>> GetPlanChanges(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? planId,
        CancellationToken cancellationToken)
    {
        if (InvalidRange(from, to, out var error))
        {
            return error;
        }

        return Ok(await reportingService.GetPlanChangesAsync(from, to, planId, cancellationToken));
    }

    /// <summary>Accepted changes taking effect between <paramref name="from"/> and <paramref name="to"/>.</summary>
    [HttpGet("scheduled-changes")]
    public async Task<ActionResult<ReportResult<ScheduledChangeRowDto>>> GetScheduledChanges(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken cancellationToken)
    {
        if (InvalidRange(from, to, out var error))
        {
            return error;
        }

        return Ok(await reportingService.GetScheduledChangesAsync(from, to, cancellationToken));
    }

    /// <summary>Open invoices with a balance, oldest due first.</summary>
    [HttpGet("receivables")]
    public async Task<ActionResult<ReportResult<ReceivableRowDto>>> GetReceivables(
        [FromQuery] decimal minOutstanding, [FromQuery] bool overdueOnly, CancellationToken cancellationToken) =>
        Ok(await reportingService.GetReceivablesAsync(minOutstanding, overdueOnly, cancellationToken));

    /// <summary>
    /// Organizations with money outstanding, worst first. Defaults to overdue-only - this is the chase
    /// list, and somebody whose invoice is due next week isn't on it.
    /// </summary>
    [HttpGet("delinquent-accounts")]
    public async Task<ActionResult<ReportResult<DelinquentAccountRowDto>>> GetDelinquentAccounts(
        [FromQuery] decimal minOutstanding, [FromQuery] bool? overdueOnly, CancellationToken cancellationToken) =>
        Ok(await reportingService.GetDelinquentAccountsAsync(
            minOutstanding, overdueOnly ?? true, cancellationToken));

    /// <summary>The plans offered as a dropdown filter on the reports that take one.</summary>
    [HttpGet("plan-options")]
    public async Task<ActionResult<IReadOnlyList<ReportFilterOptionDto>>> GetPlanOptions(
        CancellationToken cancellationToken) =>
        Ok(await reportingService.GetPlanFilterOptionsAsync(cancellationToken));

    private bool InvalidRange(DateOnly from, DateOnly to, out ActionResult error)
    {
        if (to < from)
        {
            error = BadRequest("The end of the date range can't be before the start.");
            return true;
        }

        error = null!;
        return false;
    }
}
