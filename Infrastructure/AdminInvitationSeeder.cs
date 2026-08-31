// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Ensures a single-use invitation code exists for the platform admin to claim their own account
/// with, on a fresh deployment.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The problem this solves:</strong> with "Require invitation codes to register" (see
/// <see cref="PlatformSettingsRegistry.InvitationCodesEnabled"/>) defaulting to <c>true</c>, a
/// brand-new deployment has zero invitation codes and zero accounts - nobody can register, including
/// the person who's supposed to become the platform admin, and
/// <see cref="Controllers.InvitationCodesController"/> (which mints
/// more codes) itself requires already being signed in as one. There's no way in, self-service or
/// otherwise, without this.
/// </para>
/// <para>
/// <strong>What it does:</strong> given a non-empty <paramref name="adminEmail"/> and
/// <paramref name="seedInvitationCode"/> (both read directly from <c>RUSTARCHON_ADMIN_EMAIL</c>/
/// <c>RUSTARCHON_ADMIN_CODE</c> in <c>Program.cs</c> - see their remarks there), ensures exactly one
/// <see cref="InvitationCode"/> row exists with that code, bound to that email - so a leaked/guessed
/// code is useless without also controlling that inbox. The operator sets both in <c>.env</c> (see
/// <c>.env.example</c>), starts the stack, and registers at <c>/Account/Register</c> with that
/// email/code pair - they end up an Owner of their own tenant (the normal sign-up flow) *and* a
/// platform admin (<see cref="Controllers.AccountBootstrapController"/> grants them the global
/// "Site Admin" role - see <see cref="SiteAdminRoleSeeder"/> - because their email matches this same
/// <c>RUSTARCHON_ADMIN_EMAIL</c>), with no separate "create admin user" code path needed.
/// </para>
/// <para>
/// <strong>Idempotent, not a live toggle:</strong> runs on every startup (see <c>Program.cs</c>,
/// alongside the migration step) but only inserts the row if a code with that exact value doesn't
/// already exist - it never reactivates or re-binds one that's already been redeemed, and changing
/// <c>RUSTARCHON_ADMIN_CODE</c> later just seeds an additional code rather than replacing the
/// first. Bypasses <c>IInvitationCodeRepository</c> and writes to <see cref="ApiDbContext"/> directly,
/// the same way <c>Database.Migrate()</c> does in <c>Program.cs</c> - there's no authenticated user or
/// tenant context at this point in startup for the normal repository audit-field plumbing to use.
/// </para>
/// </remarks>
public static class AdminInvitationSeeder
{
    public static async Task SeedAsync(
        ApiDbContext dbContext,
        string? adminEmail,
        string? seedInvitationCode,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(seedInvitationCode))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            logger.LogWarning(
                "RUSTARCHON_ADMIN_CODE is set but RUSTARCHON_ADMIN_EMAIL is empty - skipping seeding, " +
                "since a bootstrap code needs an email to bind to.");
            return;
        }

        // Matches InvitationCodesController's own normalization (trim/upper) - InvitationsController's
        // redeem comparison additionally strips dashes from both sides, so a code with or without
        // dashes here behaves identically either way.
        var code = seedInvitationCode.Trim().ToUpperInvariant();

        var alreadyExists = await dbContext.InvitationCodes.AnyAsync(c => c.Code == code);
        if (alreadyExists)
        {
            return;
        }

        dbContext.InvitationCodes.Add(new InvitationCode
        {
            Code = code,
            BoundEmail = adminEmail.Trim().ToLowerInvariant(),
            Note = "Seeded automatically from RUSTARCHON_ADMIN_CODE - see AdminInvitationSeeder.",
            IsActive = true,
            // No authenticated actor exists at startup - Guid.Empty marks this as system-seeded rather
            // than attributing it to whichever user happens to redeem it or leaving it unset.
            CreatedById = Guid.Empty,
            CreatedOn = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Seeded a bootstrap invitation code for {AdminEmail} - register at /Account/Register with " +
            "that email and the code from RUSTARCHON_ADMIN_CODE to claim the platform-admin account.",
            adminEmail);
    }
}
