// Copyright ©2026 Scott Blomfield

using System;

namespace RustArchon.Api.Billing;

/// <summary>
/// Thrown by <see cref="StripeTaxService.CalculateAsync"/> when Stripe Tax reports that the jurisdiction
/// a customer is in genuinely owes tax on this sale, but RustArchon has no active tax registration there
/// to collect it - Stripe's <c>not_collecting</c> <c>taxability_reason</c> (see that class's own
/// remarks for why this is the one reason worth distinguishing from every other way a calculation can
/// come back untaxed).
/// </summary>
/// <remarks>
/// Deliberately a distinct type from the plain <c>StripeException</c> a transient API failure raises.
/// Both are left to propagate out of <see cref="StripeTaxService.CalculateAsync"/> and
/// <see cref="InvoiceService.IssueForPeriodAsync"/> the same way - neither issues an invoice with wrong
/// tax on it - but <see cref="Infrastructure.SubscriptionScheduleService"/> reacts to this one
/// differently: it is not a bug to log and forget, it is an expected, ongoing operational state worth
/// recording (see <see cref="Data.BlockedInvoiceIssuance"/>) and telling a site admin about, since only a
/// human registering in Stripe's Dashboard resolves it - retrying the same request hourly never will.
/// </remarks>
public sealed class TaxJurisdictionUnregisteredException(string country, string? state)
    : Exception(
        state is null
            ? $"No active Stripe tax registration for {country} - tax is owed there but can't be collected."
            : $"No active Stripe tax registration for {state}, {country} - tax is owed there but can't be collected.")
{
    /// <summary>ISO 3166-1 alpha-2 country code the blocked calculation was for.</summary>
    public string Country { get; } = country;

    /// <summary>State/province, when the jurisdiction has one - null for a country-level block.</summary>
    public string? State { get; } = state;
}
