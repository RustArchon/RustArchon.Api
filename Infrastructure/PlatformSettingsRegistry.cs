// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// The single place every platform-wide setting RustArchon knows about is declared, and the seeder
/// that ensures each one exists in <see cref="ApiDbContext.PlatformSettings"/> with its default value.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <see cref="Data.PlatformSetting"/>'s generic key/value shape actually usable:
/// adding a new setting is adding one <c>EnsureSettingAsync</c> call below (a code change, but never a
/// migration) - the admin UI and <see cref="IPlatformSettingsCache"/> both work against whatever rows
/// exist without needing to know the full set of keys in advance.
/// </para>
/// <para>
/// <strong>Idempotent</strong>, safe to call on every Api startup (mirrors <see cref="AdminInvitationSeeder"/>/
/// <see cref="SiteAdminRoleSeeder"/>) - each setting is only ever inserted once; an admin's later edit
/// through <see cref="Controllers.PlatformSettingsController"/> is never overwritten by a later
/// restart re-running this.
/// </para>
/// </remarks>
public static class PlatformSettingsRegistry
{
    /// <summary>
    /// Whether registration requires a valid invitation code. Replaces the old
    /// <c>RUSTARCHON_INVITATION_CODES_ENABLED</c> environment-variable toggle - see
    /// <see cref="Controllers.InvitationsController"/>, the only place this is read.
    /// </summary>
    public const string InvitationCodesEnabled = "InvitationCodesEnabled";

    /// <summary>
    /// Which <see cref="Plan"/> a brand-new Organization is assigned on sign-up - see
    /// <c>AccountBootstrapController</c>, the only place this is read. Empty (the seeded default) means
    /// "no explicit choice made yet" - bootstrap falls back to the cheapest currently-active Plan in
    /// that case (see <c>IPlanRepository.GetCheapestActiveAsync</c>). Once an admin does pick one here,
    /// that exact Plan is used even if it's later deactivated - the picker in the admin UI keeps
    /// showing it in that case specifically so the admin can see (and change) what's actually
    /// configured rather than it silently reverting to the cheapest-active fallback.
    /// </summary>
    public const string DefaultPlanId = "DefaultPlanId";

    public static async Task EnsureDefaultsAsync(ApiDbContext dbContext, IConfiguration configuration, ILogger logger)
    {
        // A deployment that already had RUSTARCHON_INVITATION_CODES_ENABLED set keeps that exact
        // value as this setting's seeded starting point, the one time this row is created - the env
        // var is never consulted again after that. Defaults to true (fail closed) if neither this row
        // nor the legacy env var exist yet, matching InvitationCodeOptions's old default.
        var legacyEnvDefault = configuration.GetValue<bool?>("RUSTARCHON_INVITATION_CODES_ENABLED") ?? true;

        await EnsureSettingAsync(
            dbContext,
            key: InvitationCodesEnabled,
            displayName: "Require invitation codes to register",
            description: "When enabled, a valid invitation code is required to create an account. " +
                "Disable once you're ready to open registration to everyone.",
            valueType: PlatformSettingValueType.Boolean,
            defaultValue: legacyEnvDefault ? "true" : "false",
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: DefaultPlanId,
            displayName: "Default plan for new sign-ups",
            description: "Which plan a brand-new Organization starts on. Leave unset to always use " +
                "whichever active plan is currently cheapest.",
            valueType: PlatformSettingValueType.PlanReference,
            defaultValue: string.Empty,
            logger: logger);
    }

    private static async Task EnsureSettingAsync(
        ApiDbContext dbContext,
        string key,
        string displayName,
        string description,
        PlatformSettingValueType valueType,
        string defaultValue,
        ILogger logger)
    {
        var exists = await dbContext.Set<PlatformSetting>().AnyAsync(s => s.Key == key);
        if (exists)
        {
            return;
        }

        dbContext.Set<PlatformSetting>().Add(new PlatformSetting
        {
            Key = key,
            DisplayName = displayName,
            Description = description,
            ValueType = valueType,
            Value = defaultValue,
            CreatedById = Guid.Empty,
            CreatedOn = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Seeded platform setting '{Key}' = '{Value}'.", key, defaultValue);
    }
}
