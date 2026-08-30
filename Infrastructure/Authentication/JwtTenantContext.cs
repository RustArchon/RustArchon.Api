// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Http;

namespace RustArchon.Api.Infrastructure.Authentication;

/// <summary>
/// Provides access to the current tenant from the <c>tenant_id</c> JWT claim in API requests. See
/// JumpStart's multi-tenancy documentation (ADR-010/ADR-015).
/// </summary>
/// <remarks>
/// The <c>tenant_id</c> claim is stamped onto the real token only after <c>TokenController.Exchange</c>
/// independently verifies tenant membership server-side - by the time this class reads it, it has
/// already been validated once. This class is purely a read of an already-trusted claim, mirroring
/// <see cref="ApiUserContext"/>'s relationship to <c>ClaimTypes.NameIdentifier</c>.
/// </remarks>
public class JwtTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public JwtTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc />
    public Task<Guid?> GetCurrentTenantIdAsync()
    {
        var tenantClaim = _httpContextAccessor.HttpContext?.User.FindFirst("tenant_id")?.Value;

        return Task.FromResult(Guid.TryParse(tenantClaim, out var tenantId) ? tenantId : (Guid?)null);
    }
}
