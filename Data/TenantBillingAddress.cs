// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// An Organization's own billing address - not tenant-scoped through JumpStart's global filter, the
/// same reasoning as <see cref="Invoice"/> and <see cref="Subscription"/> (see their own remarks):
/// future billing-side code (tax calculation at invoice issuance) reads this with no ambient tenant,
/// same as the rest of the billing subsystem already does.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated table, not fields added to <see cref="Tenant"/> - RustArchon doesn't subclass or extend
/// JumpStart's own framework entities (see <c>Subscription</c>'s own remarks on the same policy, applied
/// there); every RustArchon-owned concern that relates to a tenant references <c>TenantId</c> instead.
/// </para>
/// <para>
/// <strong>Exists for tax jurisdiction, not shipping.</strong> RustArchon sells no physical goods - the
/// only reason this table exists at all is that Stripe Tax (and any sales-tax calculation generally)
/// needs to know where a customer is to determine what, if anything, is owed. That's also why every
/// field but <see cref="Country"/> is optional: a country alone is enough to know "no US nexus, no
/// tax" for most Organizations, and the more precise fields only sharpen the answer where they matter
/// (a US state, in particular).
/// </para>
/// <para>
/// One row per tenant, mutable in place - unlike <see cref="Subscription"/>'s "close and open a new
/// row" history model, there's no accounting reason to keep a past address once it's been corrected.
/// If a future need arises to know what address was on file when a specific invoice was taxed, that
/// belongs on the invoice itself as a snapshot, not as history on this table.
/// </para>
/// </remarks>
[Table("TenantBillingAddress")]
[Index(nameof(TenantId), IsUnique = true, Name = "IX_TenantBillingAddress_TenantId")]
public class TenantBillingAddress : Entity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    [MaxLength(200)]
    public string? Line1 { get; set; }

    [MaxLength(200)]
    public string? Line2 { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    /// <summary>State/province/region - free text, since the right format varies by country (a US
    /// state code, a Canadian province, nothing at all in many countries).</summary>
    [MaxLength(100)]
    public string? State { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g. <c>"US"</c>) - the one field this table treats as
    /// required once a row exists at all, since tax jurisdiction starts here and nothing downstream can
    /// answer "where is this Organization" without it.
    /// </summary>
    [Required]
    [MaxLength(2)]
    public string Country { get; set; } = string.Empty;
}
