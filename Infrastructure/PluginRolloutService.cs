// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>Where a version's staged roll-out stands.</summary>
/// <param name="StartedAtUtc">When the version first became the one being served for automatic installation.</param>
/// <param name="Hours">How long the roll-out takes (the Platform Setting at the moment of asking); zero means everyone at once.</param>
/// <param name="Fraction">How much of the fleet is eligible now: 0 to 1.</param>
public sealed record PluginRolloutStatus(DateTimeOffset StartedAtUtc, int Hours, double Fraction)
{
    public bool Complete => Fraction >= 1.0;

    /// <summary>When everyone is eligible; null when there is no ramp (everyone at once).</summary>
    public DateTimeOffset? CompleteAtUtc => Hours > 0 ? StartedAtUtc.AddHours(Hours) : null;
}

/// <summary>The staged roll-out of automatic plugin updates: a newly served version reaches servers gradually, not all at once.</summary>
public interface IPluginRollout
{
    /// <summary>
    /// Where the roll-out of <paramref name="version"/> stands, starting it (recording <paramref name="now"/> as its start) if this is the first time
    /// it is asked about. What the automatic updater calls.
    /// </summary>
    Task<PluginRolloutStatus> BeginAsync(PluginReleaseKind kind, string version, DateTimeOffset now);

    /// <summary>The same, without starting anything: <c>null</c> when the version has never been offered for automatic installation.</summary>
    Task<PluginRolloutStatus?> PeekAsync(PluginReleaseKind kind, string version, DateTimeOffset now);
}

/// <inheritdoc cref="IPluginRollout" />
/// <remarks>
/// <para>
/// A ramp, not a switch. The Platform Setting <see cref="PlatformSettingsRegistry.PluginRolloutHours"/> is how many hours a new version takes to
/// become eligible on every server; the eligible share grows in a straight line from nothing at the start to everyone at the end. Which servers
/// come first is fixed by a hash of the server, the file and the version, so it is the same on every pass and on every Api instance, needs no
/// bookkeeping, and is a different order for each version (the same servers are not always the guinea pigs).
/// </para>
/// <para>
/// It only paces <b>automatic</b> updates. A person pressing Update is never held back, and neither are the checks any update goes through. With
/// the setting at zero (the default) there is no ramp and everything is eligible at once, as before.
/// </para>
/// </remarks>
public class PluginRolloutService(ApiDbContext context, IPlatformSettingsCache settings) : IPluginRollout
{
    public async Task<PluginRolloutStatus> BeginAsync(PluginReleaseKind kind, string version, DateTimeOffset now)
    {
        var row = await context.PluginRollouts.AsNoTracking().FirstOrDefaultAsync(r => r.Kind == kind && r.Version == version);
        if (row is null)
        {
            row = new PluginRollout { Kind = kind, Version = version, StartedAtUtc = now };
            context.PluginRollouts.Add(row);
            try
            {
                await context.SaveChangesAsync();
                context.Entry(row).State = EntityState.Detached;
            }
            catch (DbUpdateException)
            {
                // Another instance began it in the same moment (kind and version are unique); its start is the start.
                context.ChangeTracker.Clear();
                row = await context.PluginRollouts.AsNoTracking().FirstAsync(r => r.Kind == kind && r.Version == version);
            }
        }

        return await StatusAsync(row.StartedAtUtc, now);
    }

    public async Task<PluginRolloutStatus?> PeekAsync(PluginReleaseKind kind, string version, DateTimeOffset now)
    {
        var row = await context.PluginRollouts.AsNoTracking().FirstOrDefaultAsync(r => r.Kind == kind && r.Version == version);
        return row is null ? null : await StatusAsync(row.StartedAtUtc, now);
    }

    /// <summary>
    /// Whether this server is among those eligible when <paramref name="fraction"/> of them are. Everyone is at 1; nobody is at 0; in between,
    /// the servers whose place in this version's order falls below the fraction.
    /// </summary>
    public static bool Includes(Guid serverId, PluginReleaseKind kind, string version, double fraction) =>
        fraction >= 1.0 || Position(serverId, kind, version) < fraction;

    /// <summary>A server's place in one version's order, from 0 (first) up to but not including 1. Stable for the same three inputs.</summary>
    public static double Position(Guid serverId, PluginReleaseKind kind, string version)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{serverId:N}|{kind}|{version}"));
        return BitConverter.ToUInt64(hash, 0) / (ulong.MaxValue + 1.0);
    }

    private async Task<PluginRolloutStatus> StatusAsync(DateTimeOffset startedAtUtc, DateTimeOffset now)
    {
        var hours = await settings.GetNonNegativeInt32Async(PlatformSettingsRegistry.PluginRolloutHours, PlatformSettingsRegistry.DefaultPluginRolloutHours);
        var fraction = hours == 0 ? 1.0 : Math.Clamp((now - startedAtUtc).TotalHours / hours, 0.0, 1.0);
        return new PluginRolloutStatus(startedAtUtc, hours, fraction);
    }
}
