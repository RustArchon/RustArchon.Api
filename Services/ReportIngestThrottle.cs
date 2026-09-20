// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Concurrent;

namespace RustArchon.Api.Services;

/// <summary>
/// A per-server ceiling on how fast reports are accepted, so one server (or whoever holds its address) cannot fill the table.
/// </summary>
/// <remarks>
/// Applied only <em>after</em> the address's secret has been checked, never before: a limiter in front of authentication would let
/// anyone who merely knows a server id use up that server's budget and lock its real reports out. A fixed window is enough - a
/// real server files a handful of reports a minute at the very most.
/// </remarks>
public interface IReportIngestThrottle
{
    /// <summary>Takes one report's worth of budget for <paramref name="serverId"/>. <c>false</c> when it has run out for now.</summary>
    bool TryAcquire(Guid serverId);
}

/// <inheritdoc />
public class ReportIngestThrottle(TimeProvider clock) : IReportIngestThrottle
{
    /// <summary>Reports accepted per server per window.</summary>
    public const int PerWindow = 60;

    /// <summary>The window's length.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    /// <inheritdoc />
    public bool TryAcquire(Guid serverId)
    {
        var now = clock.GetUtcNow();
        var bucket = _buckets.GetOrAdd(serverId, _ => new Bucket(now));

        lock (bucket)
        {
            if (now - bucket.WindowStart >= Window)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }

            if (bucket.Count >= PerWindow)
            {
                return false;
            }

            bucket.Count++;
            return true;
        }
    }

    private sealed class Bucket(DateTimeOffset start)
    {
        public DateTimeOffset WindowStart { get; set; } = start;
        public int Count { get; set; }
    }
}
