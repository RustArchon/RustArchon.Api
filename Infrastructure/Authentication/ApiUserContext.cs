// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Http;

namespace RustArchon.Api.Infrastructure.Authentication;

/// <summary>
/// Provides access to the current authenticated user from the JWT bearer token in API requests, for
/// automatic audit tracking (<c>CreatedById</c>/<c>ModifiedById</c>).
/// </summary>
public class ApiUserContext : IUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ApiUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc />
    public Task<Guid?> GetCurrentUserIdAsync()
    {
        var user = _httpContextAccessor.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult<Guid?>(null);
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Task.FromResult(Guid.TryParse(userIdClaim, out var userId) ? (Guid?)userId : null);
    }
}
