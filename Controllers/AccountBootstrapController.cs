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
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

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
    private readonly IPlanRepository _planRepository;
    private readonly ITenantPlanRepository _tenantPlanRepository;
    private readonly IPlatformSettingsCache _settingsCache;
    private readonly ApiDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountBootstrapController> _logger;

    public AccountBootstrapController(
        ITenantRepository tenantRepository,
        IUserTenantRepository userTenantRepository,
        IRoleRepository roleRepository,
        IPlanRepository planRepository,
        ITenantPlanRepository tenantPlanRepository,
        IPlatformSettingsCache settingsCache,
        ApiDbContext dbContext,
        IConfiguration configuration,
        ILogger<AccountBootstrapController> logger)
    {
        _tenantRepository = tenantRepository ?? throw new ArgumentNullException(nameof(tenantRepository));
        _userTenantRepository = userTenantRepository ?? throw new ArgumentNullException(nameof(userTenantRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _planRepository = planRepository ?? throw new ArgumentNullException(nameof(planRepository));
        _tenantPlanRepository = tenantPlanRepository ?? throw new ArgumentNullException(nameof(tenantPlanRepository));
        _settingsCache = settingsCache ?? throw new ArgumentNullException(nameof(settingsCache));
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

        // Every Organization must have a Plan from the moment it exists - see TenantPlan's remarks.
        // Which one a brand-new Organization starts on: the site admin's explicit choice (see
        // PlatformSettingsRegistry.DefaultPlanId) if they've made one - honored even if that Plan has
        // since been deactivated, since that's a deliberate admin decision, not a stale reference - or
        // otherwise the cheapest currently-active Plan (no more hardcoded "Wood" - see Plan.Name's
        // remarks on why Type went away). Upgrading is a separate, later action, not something
        // bootstrap decides. No usable Plan at all means the deployment's seed data is broken
        // (PlanSeeder should have guaranteed at least one exists) - failing loudly here is deliberate
        // rather than silently leaving the tenant planless, even though the caller
        // (NewTenantBootstrapper) treats this whole endpoint as best-effort and will just log it.
        var defaultPlanIdRaw = await _settingsCache.GetStringAsync(PlatformSettingsRegistry.DefaultPlanId);
        Plan? startingPlan = null;
        if (!string.IsNullOrWhiteSpace(defaultPlanIdRaw) && Guid.TryParse(defaultPlanIdRaw, out var defaultPlanId))
        {
            startingPlan = await _planRepository.GetByIdAsync(defaultPlanId, null);
        }

        startingPlan ??= await _planRepository.GetCheapestActiveAsync()
            ?? throw new InvalidOperationException("No usable Plan exists - check PlanSeeder ran and at least one Plan is still active.");

        await _tenantPlanRepository.AddAsync(new TenantPlan
        {
            TenantId = tenant.Id,
            PlanId = startingPlan.Id,
            AssignedAtUtc = DateTimeOffset.UtcNow
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
