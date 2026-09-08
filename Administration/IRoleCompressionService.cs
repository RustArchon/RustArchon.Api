// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RustArchon.Api.Administration;

/// <summary>What compressing an Organization would do, or did.</summary>
/// <param name="MembersPromoted">Members who end up holding the built-in Owner role.</param>
/// <param name="RolesRemoved">The Organization's own roles that are retired.</param>
public readonly record struct RoleCompressionResult(int MembersPromoted, int RolesRemoved)
{
    /// <summary>Whether there is anything to compress at all.</summary>
    public bool IsNeeded => RolesRemoved > 0;
}

/// <summary>
/// Collapsing an Organization onto the single built-in Owner role when it moves to a plan without
/// role separation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everyone becomes an Owner.</strong> That is not a special downgrade rule so much as what
/// the lower tiers already mean: a plan without role separation has exactly one role, so every member
/// holds it. It is worth stating plainly as a product decision, because it is one - on such a plan
/// every member has full control, billing included, and <c>Plan.MaximumUsers</c> is ten on Metal.
/// </para>
/// <para>
/// <strong>It happens weeks after it is agreed to.</strong> A downgrade is deferred to the end of the
/// billing period, so this runs from
/// <see cref="Infrastructure.SubscriptionScheduleService"/> when the change actually lands. The set
/// of people promoted is therefore whoever is a member <em>then</em> - which is why
/// <see cref="CompressAsync"/> re-derives it rather than accepting a list computed at the time the
/// customer clicked through the warning.
/// </para>
/// <para>
/// <strong>Roles are soft-deleted; grants are not.</strong> Keeping the definitions costs nothing and
/// lets a later upgrade offer "restore the three roles you had before". The <c>UserRole</c> rows must
/// genuinely go, or nothing was compressed.
/// </para>
/// </remarks>
public interface IRoleCompressionService
{
    /// <summary>
    /// What compression would do to this Organization right now, without doing it.
    /// </summary>
    /// <remarks>
    /// Drives the warning shown before a downgrade is accepted. Its numbers are a forecast, not a
    /// promise - see this interface's remarks on timing.
    /// </remarks>
    Task<RoleCompressionResult> PreviewAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants every member the built-in Owner role and retires the Organization's own roles.
    /// </summary>
    Task<RoleCompressionResult> CompressAsync(
        Guid tenantId, string reason, CancellationToken cancellationToken = default);
}
