// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IOrganizationSettingsService" />
public class OrganizationSettingsService(ApiDbContext dbContext) : IOrganizationSettingsService
{
    /// <inheritdoc />
    public async Task<OrganizationSettingsDto?> GetAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Set<Tenant>()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Name, t.ContactEmail })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        var billing = await dbContext.Set<TenantBillingAddress>()
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);

        return new OrganizationSettingsDto
        {
            Name = tenant.Name,
            ContactEmail = tenant.ContactEmail,
            BillingLine1 = billing?.Line1,
            BillingLine2 = billing?.Line2,
            BillingCity = billing?.City,
            BillingState = billing?.State,
            BillingPostalCode = billing?.PostalCode,
            BillingCountry = billing?.Country
        };
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid tenantId, string name, string? contactEmail,
        string? billingLine1, string? billingLine2, string? billingCity, string? billingState,
        string? billingPostalCode, string? billingCountry,
        CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Set<Tenant>()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return false;
        }

        tenant.Name = name;
        tenant.ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail;

        var billing = await dbContext.Set<TenantBillingAddress>()
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);

        if (string.IsNullOrWhiteSpace(billingCountry))
        {
            // Blank country clears the whole address, existing row and all - see this method's own
            // remarks. A row with every field but Country blank is not a meaningfully different state
            // from no row at all, and leaving one behind would be the one case GetAsync's mapping above
            // has to guess whether "no country" means "never set" or "cleared."
            if (billing is not null)
            {
                dbContext.Set<TenantBillingAddress>().Remove(billing);
            }
        }
        else
        {
            if (billing is null)
            {
                billing = new TenantBillingAddress { TenantId = tenantId };
                dbContext.Set<TenantBillingAddress>().Add(billing);
            }

            billing.Line1 = string.IsNullOrWhiteSpace(billingLine1) ? null : billingLine1.Trim();
            billing.Line2 = string.IsNullOrWhiteSpace(billingLine2) ? null : billingLine2.Trim();
            billing.City = string.IsNullOrWhiteSpace(billingCity) ? null : billingCity.Trim();
            billing.State = string.IsNullOrWhiteSpace(billingState) ? null : billingState.Trim();
            billing.PostalCode = string.IsNullOrWhiteSpace(billingPostalCode) ? null : billingPostalCode.Trim();
            billing.Country = billingCountry.Trim().ToUpperInvariant();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
