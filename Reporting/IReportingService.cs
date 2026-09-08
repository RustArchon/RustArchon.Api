// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Reporting;

/// <summary>
/// Platform-wide operational reports - the cross-tenant views a platform admin runs the business from.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="Billing.ISubscriptionService"/>, which is a <em>tenant's</em>
/// view of its own subscription and takes a tenant id on every call. Nothing here takes one: these read
/// across every Organization, and access is gated at the controller by the <c>ViewReports</c> policy
/// rather than by tenant membership.
/// </para>
/// <para>
/// Each method returns a fully-formed <see cref="ReportResult{TRow}"/> - rows, summary and all - because
/// the UI is only one of the things that consumes it. See <see cref="ReportResult{TRow}"/>.
/// </para>
/// <para>
/// Every report here excludes soft-deleted Organizations, which JumpStart's global filter does on its
/// own. Reports read across tenants, never across deletions.
/// </para>
/// </remarks>
public interface IReportingService
{
    /// <summary>
    /// Organizations whose billing period ends between <paramref name="from"/> and
    /// <paramref name="to"/> inclusive, soonest first: the cash due in that window, and the window in
    /// which a churn conversation is still possible.
    /// </summary>
    /// <param name="from">First renewal date to include.</param>
    /// <param name="to">Last renewal date to include. Inclusive - the whole of this day counts.</param>
    /// <param name="planId">Narrow to one plan, or <c>null</c> for all of them.</param>
    Task<ReportResult<UpcomingRenewalRowDto>> GetUpcomingRenewalsAsync(
        DateOnly from, DateOnly to, Guid? planId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Organizations created between <paramref name="from"/> and <paramref name="to"/> inclusive, and
    /// whether each one ever provisioned a server.
    /// </summary>
    Task<ReportResult<NewSignupRowDto>> GetNewSignupsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every live subscription, with its monthly value - the register the plan mix and MRR come out of.
    /// </summary>
    /// <param name="planId">Narrow to one plan, or <c>null</c> for all of them.</param>
    Task<ReportResult<SubscriptionRegisterRowDto>> GetSubscriptionsAsync(
        Guid? planId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plan moves that happened between <paramref name="from"/> and <paramref name="to"/> inclusive -
    /// upgrades, downgrades, and the drops to a free plan that stand in for churn.
    /// </summary>
    /// <param name="planId">Narrow to moves onto or off this plan, or <c>null</c> for all of them.</param>
    Task<ReportResult<PlanChangeRowDto>> GetPlanChangesAsync(
        DateOnly from, DateOnly to, Guid? planId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepted changes that haven't taken effect yet, with an effective date between
    /// <paramref name="from"/> and <paramref name="to"/> inclusive.
    /// </summary>
    Task<ReportResult<ScheduledChangeRowDto>> GetScheduledChangesAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every open invoice with money still owed on it, oldest due first - the invoice register behind
    /// collections.
    /// </summary>
    /// <param name="minOutstanding">
    /// Hides invoices owing less than this. Zero shows everything; a threshold is how a long list is
    /// reduced to the debts worth someone's time.
    /// </param>
    /// <param name="overdueOnly">
    /// When <c>true</c>, drops invoices that haven't reached their due date - the difference between
    /// "what do we expect" and "who is late".
    /// </param>
    Task<ReportResult<ReceivableRowDto>> GetReceivablesAsync(
        decimal minOutstanding = 0m, bool overdueOnly = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Organizations with money outstanding, worst first - the same debts as
    /// <see cref="GetReceivablesAsync"/> grouped per customer, with an aged breakdown on each row.
    /// </summary>
    /// <param name="minOutstanding">Hides Organizations owing less than this in total.</param>
    /// <param name="overdueOnly">
    /// When <c>true</c>, lists only Organizations with something actually past due. Defaults to true:
    /// this is the chase list, and an Organization whose only invoice is due next week does not belong
    /// on it.
    /// </param>
    Task<ReportResult<DelinquentAccountRowDto>> GetDelinquentAccountsAsync(
        decimal minOutstanding = 0m, bool overdueOnly = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// The plans available as a dropdown filter on the reports that take one - every plan any
    /// Organization is or has been on, plus every active plan.
    /// </summary>
    /// <remarks>
    /// Superseded plans are included on purpose. They stop being sellable but Organizations stay on
    /// them, so a filter that only offered the active catalog would silently be unable to narrow to a
    /// plan that still has subscribers.
    /// </remarks>
    Task<IReadOnlyList<ReportFilterOptionDto>> GetPlanFilterOptionsAsync(
        CancellationToken cancellationToken = default);
}
