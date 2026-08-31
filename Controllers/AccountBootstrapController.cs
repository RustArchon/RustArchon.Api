// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using JumpStart.MultiTenant.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Provisions a brand-new user with their own tenant and an "Owner" role scoped to it, so they can
/// start adding Rust servers immediately after registering.
/// </summary>
/// <remarks>
/// <para>
/// A new user has zero tenant memberships and zero permission claims, so every
/// <c>[EntityAuthorize]</c>-protected endpoint (including every <see cref="RustServersController"/>
/// action) would 403 for them until this runs. Protected the same way
/// <c>TokenController.Exchange</c> is (plain <see cref="AuthorizeAttribute"/>, not
/// <c>[EntityAuthorize]</c>) since a brand-new user has no permission claims to check yet.
/// </para>
/// <para>
/// Idempotent - if the calling user already belongs to a tenant, this is a no-op. Called directly
/// from <c>Register.razor</c>/<c>ExternalLogin.razor</c> right after account creation, mirroring
/// JumpStart's own <c>DemoNewUserBootstrapper</c> pattern (see its remarks).
/// </para>
/// </remarks>
[ApiController]
[Route("api/account-bootstrap")]
public class AccountBootstrapController : ControllerBase
{
    private static readonly string[] RustServerActions = ["Get", "List", "Create", "Update", "Delete"];
    private const string OwnerRoleName = "Owner";

    private readonly ITenantRepository _tenantRepository;
    private readonly IUserTenantRepository _userTenantRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly ApiDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountBootstrapController> _logger;

    public AccountBootstrapController(
        ITenantRepository tenantRepository,
        IUserTenantRepository userTenantRepository,
        IRoleRepository roleRepository,
        ApiDbContext dbContext,
        IConfiguration configuration,
        ILogger<AccountBootstrapController> logger)
    {
        _tenantRepository = tenantRepository ?? throw new ArgumentNullException(nameof(tenantRepository));
        _userTenantRepository = userTenantRepository ?? throw new ArgumentNullException(nameof(userTenantRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the calling user has a tenant of their own, creating one (plus an "Owner" role
    /// granting full access to their own Rust servers) if they don't already have one.
    /// </summary>
    /// <param name="tenantName">
    /// A display name for the new tenant, if one needs to be created (e.g. the user's email). If
    /// omitted, a generic default is used - the user can rename it later.
    /// </param>
    [HttpPost("ensure-tenant")]
    [Authorize]
    public async Task<IActionResult> EnsureTenant([FromQuery] string? tenantName)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        // Evaluated on every call, not just the very first one - unlike the tenant/Owner-role
        // provisioning below, this must not short-circuit once a tenant already exists, or it could
        // never retroactively pick up a RUSTARCHON_ADMIN_EMAIL change (or a first grant for an account
        // that registered before this existed). AssignUserToRoleAsync is itself idempotent, so calling
        // it again here on every subsequent bootstrap check is cheap and harmless. See
        // SiteAdminRoleSeeder's remarks for why this replaced a live email comparison entirely.
        var adminEmail = _configuration["RUSTARCHON_ADMIN_EMAIL"];
        var callerEmail = User.Identity?.Name;
        if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(callerEmail)
            && string.Equals(callerEmail, adminEmail, StringComparison.OrdinalIgnoreCase))
        {
            var siteAdminRoleId = await SiteAdminRoleSeeder.EnsureRoleAsync(_dbContext, _logger);
            await _roleRepository.AssignUserToRoleAsync(userId, siteAdminRoleId, tenantId: null);
        }

        var existingTenants = await _userTenantRepository.GetTenantsForUserAsync(userId);
        if (existingTenants.Count > 0)
        {
            return NoContent();
        }

        var tenant = await _tenantRepository.AddAsync(new Tenant
        {
            Name = string.IsNullOrWhiteSpace(tenantName) ? "My Organization" : tenantName,
            IsActive = true
        });

        await _userTenantRepository.AddAsync(new UserTenant { UserId = userId, TenantId = tenant.Id });

        // A tenant-scoped Owner role, not a global one - it only grants access to this tenant's own
        // RustServer records, never another tenant's.
        var ownerRole = await _roleRepository.AddAsync(new Role
        {
            Name = OwnerRoleName,
            Description = "Full access to this organization's Rust servers.",
            TenantId = tenant.Id
        });

        foreach (var action in RustServerActions)
        {
            await _roleRepository.AddPermissionAsync(ownerRole.Id, $"RustServer.{action}");
        }

        await _roleRepository.AssignUserToRoleAsync(userId, ownerRole.Id, tenantId: tenant.Id);

        return NoContent();
    }
}
