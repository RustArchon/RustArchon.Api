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

        return tenant is null
            ? null
            : new OrganizationSettingsDto { Name = tenant.Name, ContactEmail = tenant.ContactEmail };
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid tenantId, string name, string? contactEmail, CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Set<Tenant>()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return false;
        }

        tenant.Name = name;
        tenant.ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
