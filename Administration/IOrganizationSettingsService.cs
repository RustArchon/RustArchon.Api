// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// An Organization editing its own identity - the name it goes by and the address its lifecycle
/// notices go to.
/// </summary>
/// <remarks>
/// <para>
/// The customer-facing counterpart to <c>OrganizationsController</c>, which only ever reads these two
/// fields on a site admin's behalf. The tenant comes from <c>ITenantContext</c> at the controller,
/// exactly like <see cref="IOrganizationMemberService"/> - there is no way to reach another
/// Organization's row through this.
/// </para>
/// <para>
/// <see cref="JumpStart.Data.Tenant.ContactEmail"/> is not cosmetic: it is where
/// <c>OrganizationLifecycleService</c>, <c>InvoiceService</c> and <c>PaymentService</c> already send
/// every notice they queue, silently no-oping when it is blank. This is the one place an Organization
/// can set or correct it themselves, rather than asking a site admin to.
/// </para>
/// </remarks>
public interface IOrganizationSettingsService
{
    /// <summary>The caller's own Organization name and contact address.</summary>
    /// <returns><c>null</c> when the tenant cannot be found.</returns>
    Task<OrganizationSettingsDto?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Changes them.</summary>
    /// <param name="contactEmail">
    /// A blank value clears the address rather than being refused - not every Organization has to set
    /// one, and <c>OrganizationLifecycleService.NotifyAsync</c> already treats a missing address as a
    /// silent no-op rather than an error.
    /// </param>
    /// <param name="billingCountry">
    /// A blank value clears the whole billing address (every field below, not just this one) - the same
    /// "blank clears it" rule <paramref name="contactEmail"/> already follows. Anything else requires a
    /// non-blank <see cref="Data.TenantBillingAddress.Country"/> to be stored at all - see that class's
    /// remarks on why country is the one field this concern actually depends on.
    /// </param>
    /// <returns><c>false</c> when the tenant cannot be found.</returns>
    Task<bool> UpdateAsync(
        Guid tenantId, string name, string? contactEmail,
        string? billingLine1, string? billingLine2, string? billingCity, string? billingState,
        string? billingPostalCode, string? billingCountry,
        CancellationToken cancellationToken = default);
}
