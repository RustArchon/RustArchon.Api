// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Concurrent;

namespace RustArchon.Api.Services;

/// <summary>A per-server ceiling on how often an on-demand poll (<see cref="IServerPollService"/>) may be sent, so a Refresh a person keeps clicking - or
/// an automated caller - cannot turn into a steady stream of extra RCON round trips to their game server.</summary>
public interface IServerPollThrottle
{
    /// <summary>Whether a server may be polled now. Records the attempt when it may, so a call this returns <c>true</c> for counts toward the next one's wait.</summary>
    bool TryAcquire(Guid serverId);
}

/// <inheritdoc />
public class ServerPollThrottle(TimeProvider clock) : IServerPollThrottle
{
    /// <summary>The shortest gap between two on-demand polls of the same server.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    public bool TryAcquire(Guid serverId)
    {
        var now = clock.GetUtcNow();
        var bucket = _buckets.GetOrAdd(serverId, _ => new Bucket());

        lock (bucket)
        {
            if (bucket.LastUtc is { } last && now - last < MinInterval)
            {
                return false;
            }

            bucket.LastUtc = now;
            return true;
        }
    }

    private sealed class Bucket
    {
        public DateTimeOffset? LastUtc;
    }
}
