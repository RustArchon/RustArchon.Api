// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Concurrent;

namespace RustArchon.Api.Services;

/// <summary>
/// A ceiling on how often one plugin file can be manually rechecked (<see cref="IThirdPartyPluginUpdateService.RecheckFileAsync"/>), so a person
/// double-clicking - or clicking "Check again" repeatedly out of impatience - cannot turn into a rapid string of downloads from a marketplace that
/// is not ours; see <see cref="PluginFileValidationJob"/>'s "being a good guest" remarks, which this extends to the on-demand path.
/// </summary>
public interface IPluginFileRecheckThrottle
{
    /// <summary>Whether this lookup may be rechecked now. Records the attempt when it may, so it counts toward the next one's wait.</summary>
    bool TryAcquire(Guid lookupId);
}

/// <inheritdoc />
public class PluginFileRecheckThrottle(TimeProvider clock) : IPluginFileRecheckThrottle
{
    /// <summary>The shortest gap between two manual rechecks of the same file.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    public bool TryAcquire(Guid lookupId)
    {
        var now = clock.GetUtcNow();
        var bucket = _buckets.GetOrAdd(lookupId, _ => new Bucket());

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
