// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// Moving an Organization's subscription between <see cref="SubscriptionStatus"/> values, and making
/// that mean something to their running servers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the piece that can visibly break a paying customer.</strong> Suspending an account
/// tears down live RCON connections; getting it wrong takes somebody's game servers off the panel. So
/// the transitions are explicit, every one records who did it and why
/// (<c>Subscription.StatusReason</c>), and the two that touch connections do the least surprising thing
/// possible - see below.
/// </para>
/// <para>
/// <strong>No new message contract, and no Worker change.</strong> The obvious design was a
/// <c>TenantSuspended</c> event with a matching consumer, and it would have been more code in three
/// repositories to reach an outcome the Worker already implements: suspension publishes the existing
/// per-server <c>ServerLifecycleChanged(Disabled)</c>, which is exactly what the customer disabling
/// each server by hand would send, and reinstatement publishes the existing <c>ConnectToServer</c>.
/// A worker cannot tell the two apart and has no reason to - "stop this connection" is one job however
/// it was decided. The only thing the platform-level state has to do beyond that is stop the claim
/// sweep putting the connections straight back, which is a query filter in the Api, not a message.
/// </para>
/// <para>
/// <strong><c>RustServer.IsEnabled</c> is never touched.</strong> That field is the customer's own
/// intent, and overwriting it would lose which servers they wanted running - so reinstatement would
/// either restore too many or too few. Suspension leaves it alone and stops the connections around it,
/// so reinstating restores exactly the set that was live before.
/// </para>
/// </remarks>
public interface IOrganizationLifecycleService
{
    /// <summary>
    /// Moves the Organization's open subscription to <paramref name="status"/>, applying whatever that
    /// means for their servers.
    /// </summary>
    /// <remarks>
    /// <see cref="SubscriptionStatus.Suspended"/> stops every connection;
    /// <see cref="SubscriptionStatus.Active"/> and <see cref="SubscriptionStatus.PastDue"/> restore the
    /// ones the customer has enabled. PastDue deliberately keeps servers running: it means money is
    /// owed, not that service has stopped, and conflating the two would make every late invoice an
    /// outage.
    /// </remarks>
    /// <returns><c>false</c> when the Organization has no open subscription.</returns>
    Task<bool> SetStatusAsync(
        Guid tenantId, SubscriptionStatus status, string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the Organization: closes the open subscription, stops its servers, and soft-deletes the
    /// tenant.
    /// </summary>
    /// <remarks>
    /// The soft delete is what makes cancellation stick. <see cref="Infrastructure.SubscriptionBackfiller"/>
    /// hands a plan to every tenant with no open subscription - correctly, since a planless tenant is
    /// normally a bug - and would otherwise re-subscribe everyone who ever left on the next startup. A
    /// soft-deleted tenant is invisible to it through JumpStart's global filter.
    /// </remarks>
    /// <param name="tenantId">The Organization to cancel.</param>
    /// <param name="category">Picks which notice email is sent - see <see cref="CancellationReasonCategory"/>.</param>
    /// <param name="reason">Optional extra detail, included in the notice alongside <paramref name="category"/>.</param>
    Task<bool> CancelAsync(
        Guid tenantId, CancellationReasonCategory category, string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>Brings a cancelled Organization back, on the plan a site admin chooses.</summary>
    Task<bool> ReopenAsync(
        Guid tenantId, Guid planId, int termMonths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an already-active Organization onto a different plan immediately - no upgrade/downgrade
    /// timing decision, no proration, no invoice, and no refund, unlike a tenant's own self-service
    /// change (<see cref="Billing.ISubscriptionService.ApplyAsync"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an admin correction or a comped plan, not a customer-facing feature. What stays enforced:
    /// the target plan must exist and be <c>Active</c>, and it must be able to hold the Organization's
    /// current server count. What a "force" deliberately skips: the price-comparison timing rule, all
    /// proration math, any invoice, and <see cref="Plan.OnePerOwner"/>.
    /// </para>
    /// <para>
    /// Any pending <c>ScheduledPlanChange</c> the tenant already had queued is cancelled as a side
    /// effect, so it can't silently re-apply on top of the forced plan later.
    /// </para>
    /// </remarks>
    /// <param name="quantity">Slots to hold, or null to derive them the same way a normal change would.</param>
    /// <param name="reason">Required - see <see cref="Data.Subscription.PlanChangeReason"/>.</param>
    Task<AdminForcePlanChangeResult> ForcePlanChangeAsync(
        Guid tenantId, Guid planId, int termMonths, int? quantity, string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IOrganizationLifecycleService.ForcePlanChangeAsync"/>.</summary>
/// <param name="Success">Whether the plan was actually changed.</param>
/// <param name="Error">
/// Why not, when <paramref name="Success"/> is false - specific enough to show an admin directly (e.g.
/// naming the server-count shortfall), the same way the tenant-facing quote's own
/// <c>PlanChangeQuoteDto.BlockedReason</c> is.
/// </param>
public sealed record AdminForcePlanChangeResult(bool Success, string? Error);
