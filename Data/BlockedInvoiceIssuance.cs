// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// Records that a tenant's current billing period couldn't be invoiced because Stripe Tax reports tax
/// is owed in their jurisdiction but RustArchon has no active registration there yet - see
/// <see cref="Billing.TaxJurisdictionUnregisteredException"/> and
/// <see cref="Infrastructure.SubscriptionScheduleService"/>, the only writer of this table.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One row per tenant, not one per failed attempt.</strong> The hourly sweep re-attempts a
/// blocked period every pass (see <see cref="Infrastructure.SubscriptionScheduleService"/>'s own
/// remarks on why that retry is now immediate rather than a term away), so a row here is upserted in
/// place - <see cref="LastAttemptOn"/>/<see cref="AttemptCount"/> advance, <see cref="FirstBlockedOn"/>
/// never moves - rather than accumulating one row per hour a jurisdiction stays unregistered. The row is
/// deleted outright the moment that tenant's period is successfully billed, which is what lets both the
/// admin banner and the daily digest simply be "whatever is in this table right now".
/// </para>
/// <para>
/// Not tenant-scoped through JumpStart's global filter - the same reasoning as <see cref="Invoice"/>,
/// <see cref="Subscription"/> and <see cref="TenantBillingAddress"/> (see their own remarks): a
/// background sweep has no ambient tenant to filter by, and the admin-facing report/banner reads across
/// every tenant on purpose.
/// </para>
/// </remarks>
[Table("BlockedInvoiceIssuance")]
[Index(nameof(TenantId), IsUnique = true, Name = "IX_BlockedInvoiceIssuance_TenantId")]
public class BlockedInvoiceIssuance : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>ISO 3166-1 alpha-2 country code of the jurisdiction blocking this tenant's invoice.</summary>
    [Required]
    [MaxLength(2)]
    public string Country { get; set; } = string.Empty;

    /// <summary>State/province, when the jurisdiction has one - null for a country-level block.</summary>
    [MaxLength(100)]
    public string? State { get; set; }

    /// <summary>When this tenant's invoice was first blocked - never updated after the row is created.</summary>
    public DateTimeOffset FirstBlockedOn { get; set; }

    /// <summary>The most recent hourly pass that tried and failed to bill this tenant.</summary>
    public DateTimeOffset LastAttemptOn { get; set; }

    /// <summary>How many passes in a row have failed - a rough "how long has this been going on" figure
    /// alongside <see cref="FirstBlockedOn"/>.</summary>
    public int AttemptCount { get; set; }
}
