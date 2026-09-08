// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using JumpStart.MultiTenant.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Provisions a brand-new user with their own Organization, so they can start adding Rust servers
/// immediately after registering.
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
/// <para>
/// <strong>Somebody who was invited gets no Organization of their own.</strong> They are registering
/// in order to join one that already exists, and handing them an empty second one - which is billable,
/// occupies a free-plan slot, and appears in their switcher forever - is not what they asked for. See
/// <see cref="EnsureTenant"/>. They can still create one deliberately later, through
/// <see cref="OrganizationController"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/account-bootstrap")]
public class AccountBootstrapController : ControllerBase
{
    private readonly IOrganizationProvisioningService _provisioning;
    private readonly IUserTenantRepository _userTenantRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly ApiDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountBootstrapController> _logger;

    public AccountBootstrapController(
        IOrganizationProvisioningService provisioning,
        IUserTenantRepository userTenantRepository,
        IRoleRepository roleRepository,
        ApiDbContext dbContext,
        IConfiguration configuration,
        ILogger<AccountBootstrapController> logger)
    {
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _userTenantRepository = userTenantRepository ?? throw new ArgumentNullException(nameof(userTenantRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the calling user has somewhere to be - an Organization of their own, unless they were
    /// invited to somebody else's.
    /// </summary>
    /// <param name="tenantName">
    /// A display name for the new Organization, if one needs to be created (e.g. the user's email).
    /// If omitted, a generic default is used - the user can rename it later.
    /// </param>
    [HttpPost("ensure-tenant")]
    [Authorize]
    public async Task<IActionResult> EnsureTenant(
        [FromQuery] string? tenantName, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var callerEmail = User.Identity?.Name;

        // Evaluated on every call, not just the very first one - unlike the provisioning below, this
        // must not short-circuit once a tenant already exists, or it could never retroactively pick up
        // a RUSTARCHON_ADMIN_EMAIL change (or a first grant for an account that registered before this
        // existed). AssignUserToRoleAsSystemAsync is itself idempotent, so calling it again on every
        // subsequent bootstrap check is cheap and harmless. See SiteAdminRoleSeeder's remarks.
        var adminEmail = _configuration["RUSTARCHON_ADMIN_EMAIL"];
        if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(callerEmail)
            && string.Equals(callerEmail, adminEmail, StringComparison.OrdinalIgnoreCase))
        {
            var siteAdminRoleId = await SiteAdminRoleSeeder.EnsureRoleAsync(
                _dbContext, _roleRepository, _logger);

            // The system variant: this is the platform's very first administrator, so there is no
            // grantor who already holds these permissions. Without it, rule 4 refuses the one grant
            // that makes platform administration possible at all.
            await _roleRepository.AssignUserToRoleAsSystemAsync(userId, siteAdminRoleId, tenantId: null);
        }

        var existingTenants = await _userTenantRepository.GetTenantsForUserAsync(userId);
        if (existingTenants.Count > 0)
        {
            return NoContent();
        }

        // They registered because somebody asked them to join an Organization. Provisioning one here
        // would mean every invited person silently acquires an empty Organization they never wanted -
        // billable, holding their one free-plan slot, and in their switcher forever. Checked
        // server-side against the invitation rather than by passing a flag through registration, so
        // it holds however they arrived: a return URL that survived the round trip, one that did not,
        // or an invitation accepted days later.
        if (await HasPendingInvitationAsync(callerEmail, cancellationToken))
        {
            _logger.LogInformation(
                "Skipped provisioning an organization for {Email} - they have an invitation to join "
                + "an existing one.", callerEmail);

            return NoContent();
        }

        await _provisioning.CreateAsync(
            userId, tenantName, planId: null, enforceOnePerOwner: false, cancellationToken);

        return NoContent();
    }

    /// <summary>Whether an invitation is outstanding for this address.</summary>
    /// <remarks>
    /// Read directly rather than through <c>ITenantInvitationService</c>, which answers about one
    /// tenant or one token - neither of which this knows. The question here is only "is this person
    /// expected somewhere?".
    /// </remarks>
    private async Task<bool> HasPendingInvitationAsync(string? email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var address = email.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        return await _dbContext.Set<TenantInvitation>()
            .AcrossAllTenants()
            .AnyAsync(
                i => i.Email == address
                    && i.AcceptedOn == null
                    && i.RevokedOn == null
                    && i.ExpiresOn > now,
                cancellationToken);
    }
}
