// Copyright ©2026 Scott Blomfield

using System;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// A <see cref="TimeProvider"/> whose "now" is set by the caller - the clock
/// <see cref="DemoDataSeeder"/> runs history on.
/// </summary>
/// <remarks>
/// <para>
/// Every date the billing subsystem stamps - a period's boundaries, an invoice's issue and due dates, a
/// payment's receipt - comes from an injected <see cref="TimeProvider"/> rather than from
/// <see cref="DateTimeOffset.UtcNow"/>. That was done so proration could be tested; it is also what makes
/// a backdated history possible without a single hand-written row. Registering this in place of
/// <see cref="TimeProvider.System"/> and winding it forward replays the real services at historical
/// instants, so the data they leave behind is the data they would have left had the system been running
/// all along.
/// </para>
/// <para>
/// <strong>Development only, and only for the length of a seeding run.</strong> It is registered solely
/// when the <c>--seed-demo</c> switch is present (see <c>Program.cs</c>), and that process exits without
/// ever serving a request.
/// </para>
/// </remarks>
public sealed class SimulatedClock(DateTimeOffset start) : TimeProvider
{
    /// <summary>The instant every service resolved from the container will see as "now".</summary>
    public DateTimeOffset Now { get; set; } = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
