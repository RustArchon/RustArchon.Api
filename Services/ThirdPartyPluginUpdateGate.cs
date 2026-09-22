// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Services;

/// <summary>
/// When Rust's monthly forced wipe happens: <b>the first Thursday of every month at 19:00 London time</b> (Scott, 2026-09-21), which is
/// 18:00 UTC during British Summer Time and 19:00 UTC otherwise. It follows UK daylight saving, not US.
/// </summary>
/// <remarks>
/// Only the monthly wipe is modelled. Servers that wipe weekly do not have the problem this exists for (plugins updated ahead of a wipe in a
/// way the current server code cannot run), and a server that does not care sets its hold to 0.
/// </remarks>
public static class MonthlyWipeSchedule
{
    /// <summary>The wipe's hour, London time.</summary>
    public const int HourLocal = 19;

    /// <summary>
    /// The longest hold that can be set: three weeks. Wipes are a little under 28 to 35 days apart (the March-to-April gap loses an hour to
    /// daylight saving), so a hold of 28 would leave some servers held for good in some months; three weeks always leaves a week to update.
    /// </summary>
    public const int MaxHoldDays = 21;

    /// <summary>What a server holds for until it says otherwise.</summary>
    public const int DefaultHoldDays = 7;

    private static readonly Lazy<TimeZoneInfo> London = new(FindLondon);

    /// <summary>The wipe of the given month, as a moment in time.</summary>
    public static DateTimeOffset WipeInMonth(int year, int month)
    {
        var day = new DateTime(year, month, 1);
        while (day.DayOfWeek != DayOfWeek.Thursday)
        {
            day = day.AddDays(1);
        }

        // 19:00 is never near a clock change (those are 01:00-02:00 on a Sunday), so the local time is never ambiguous or missing.
        var local = new DateTime(day.Year, day.Month, day.Day, HourLocal, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, London.Value), TimeSpan.Zero);
    }

    /// <summary>The first wipe strictly after <paramref name="now"/> - at the moment of a wipe, that wipe has happened and this is the next month's.</summary>
    public static DateTimeOffset NextWipeAfter(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var wipe = WipeInMonth(utc.Year, utc.Month);
        if (wipe > now)
        {
            return wipe;
        }

        var next = new DateTime(utc.Year, utc.Month, 1).AddMonths(1);
        return WipeInMonth(next.Year, next.Month);
    }

    /// <summary>
    /// When the hold lifts, or <c>null</c> if nothing is held back at <paramref name="now"/>: the hold covers the <paramref name="holdDays"/> days
    /// (of 24 hours) before the next wipe, and ends at the wipe. A hold of 0 (or less) never holds anything.
    /// </summary>
    public static DateTimeOffset? HeldUntil(DateTimeOffset now, int holdDays)
    {
        if (holdDays <= 0)
        {
            return null;
        }

        var wipe = NextWipeAfter(now);
        return now >= wipe.AddDays(-holdDays) ? wipe : null;
    }

    private static TimeZoneInfo FindLondon()
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("The London time zone is not available on this machine, so the monthly wipe time cannot be worked out.");
    }
}

/// <summary>Which of the three gates decides whether a server takes third-party plugin updates automatically, in the order they are checked.</summary>
public enum ThirdPartyUpdateGateState
{
    /// <summary>All three pass: updates may be applied.</summary>
    Open,

    /// <summary>The organization's plan does not offer the feature.</summary>
    PlanDoesNotOffer,

    /// <summary>The plan offers it but this server has not opted in.</summary>
    NotOptedIn,

    /// <summary>Opted in, but the server is inside its window before the monthly wipe.</summary>
    HeldForWipe
}

/// <summary>What the gates say for one server at one moment.</summary>
/// <param name="State">The first gate that is closed, or <see cref="ThirdPartyUpdateGateState.Open"/>.</param>
/// <param name="PlanOffers">Whether the organization's plan offers the feature.</param>
/// <param name="OptedIn">Whether the server has opted in.</param>
/// <param name="HoldDays">The server's days-before-wipe setting.</param>
/// <param name="NextWipeUtc">The next monthly wipe.</param>
/// <param name="HeldUntilUtc">When the wipe window ends, if the server is inside it (whatever the other gates say); otherwise <c>null</c>.</param>
public sealed record ThirdPartyUpdateGateResult(
    ThirdPartyUpdateGateState State, bool PlanOffers, bool OptedIn, int HoldDays, DateTimeOffset NextWipeUtc, DateTimeOffset? HeldUntilUtc)
{
    public bool IsOpen => State == ThirdPartyUpdateGateState.Open;
}

/// <summary>
/// The three gates on applying updates to a server's third-party plugins automatically: the plan offers it, the server opted in, and it is
/// not inside its days-before-wipe window.
/// </summary>
public interface IThirdPartyPluginUpdateGate
{
    Task<ThirdPartyUpdateGateResult> EvaluateAsync(RustServer server, DateTimeOffset now);
}

/// <inheritdoc cref="IThirdPartyPluginUpdateGate" />
/// <remarks>
/// The plan is the one the organization is on now (its open subscription); an organization with none, or several of which any offers the
/// feature, is judged by that - it never counts as offered by default, so a missing subscription can only close the gate.
/// </remarks>
public class ThirdPartyPluginUpdateGate(ApiDbContext context) : IThirdPartyPluginUpdateGate
{
    public async Task<ThirdPartyUpdateGateResult> EvaluateAsync(RustServer server, DateTimeOffset now)
    {
        // Not tenant-filtered: this is asked by a platform job as well as by a person, and the tenant is the server's own.
        var planOffers = await context.Set<Subscription>().IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == server.TenantId && s.EndDate == null)
            .AnyAsync(s => s.Plan.OffersThirdPartyPluginUpdates);

        var heldUntil = MonthlyWipeSchedule.HeldUntil(now, server.ThirdPartyPluginUpdateHoldDays);
        var state = !planOffers ? ThirdPartyUpdateGateState.PlanDoesNotOffer
            : !server.ThirdPartyPluginUpdatesEnabled ? ThirdPartyUpdateGateState.NotOptedIn
            : heldUntil is not null ? ThirdPartyUpdateGateState.HeldForWipe
            : ThirdPartyUpdateGateState.Open;

        return new ThirdPartyUpdateGateResult(
            state, planOffers, server.ThirdPartyPluginUpdatesEnabled, server.ThirdPartyPluginUpdateHoldDays,
            MonthlyWipeSchedule.NextWipeAfter(now), heldUntil);
    }
}
