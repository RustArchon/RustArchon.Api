// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.DiagnosticLogs;

/// <summary>
/// Holds the log entries the Worker instances have forwarded (<see cref="WorkerLogBatch"/>, ADR-0006): one bounded ring per Worker instance,
/// keeping the sequence numbers the Worker assigned, so a lost batch shows up as a gap rather than being papered over.
/// </summary>
/// <remarks>
/// Each instance is its own source, <c>worker:&lt;first 8 characters of its id&gt;</c>; a restarted Worker is a new instance and so a new source, which
/// is why a reader never has to guess whether two sequence numbers belong to the same run. Only the most recently heard-from
/// <see cref="MaxInstances"/> instances are kept. Nothing here is persisted: an Api restart empties it.
/// </remarks>
public sealed class WorkerLogStore(TimeProvider? timeProvider = null)
{
    public const int MaxInstances = 8;

    public const int Capacity = 5000;

    /// <summary>How long without a batch before a source is flagged as quiet. Workers write constantly (polls, heartbeats), so silence means something.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);

    private const string NoWorkerReason =
        "No log batches have been received from any Worker since this Api started. Either no Worker is running, or log batches are not arriving "
        + "(RabbitMQ down or unreachable from the Worker or the Api). Treat a silent Worker source as a possible messaging failure, not a quiet Worker.";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Held> _instances = new(StringComparer.Ordinal);

    public static string SourceName(string instanceId) => "worker:" + (instanceId.Length > 8 ? instanceId[..8] : instanceId);

    /// <summary>Stores the batch. Returns how many entries were new (a redelivered batch adds none).</summary>
    public int Ingest(WorkerLogBatch batch)
    {
        if (string.IsNullOrEmpty(batch.InstanceId) || batch.Entries.Count == 0)
        {
            return 0;
        }

        Held held;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_instances.TryGetValue(batch.InstanceId, out held!))
            {
                held = new Held(SourceName(batch.InstanceId), new LogRing(SourceName(batch.InstanceId), batch.InstanceId, Capacity));
                _instances[batch.InstanceId] = held;
            }

            // Stamped before evicting, so the instance just heard from is never the one that looks oldest.
            held.LastReceivedUtc = now;
            EvictOldestBeyondLimit();
        }

        var stored = 0;
        foreach (var entry in batch.Entries.OrderBy(e => e.Seq))
        {
            var level = (LogLevel)Math.Clamp(entry.Level, (int)LogLevel.Debug, (int)LogLevel.Critical);
            if (held.Ring.Ingest(new LogEntry(
                    held.Name, batch.InstanceId, entry.Seq, entry.TimestampUtc, level, entry.Category, entry.Message, entry.Exception,
                    entry.Properties ?? new Dictionary<string, string>())))
            {
                stored++;
            }
        }

        return stored;
    }

    /// <summary>A source per Worker instance held, or a single unavailable one (named <c>worker</c>) saying why there is none.</summary>
    public IReadOnlyList<ILogSource> Sources()
    {
        lock (_gate)
        {
            if (_instances.Count == 0)
            {
                return [new UnavailableSource("worker", NoWorkerReason)];
            }

            return _instances.Values.Select(h => (ILogSource)new HeldSource(h, _time)).ToList();
        }
    }

    private void EvictOldestBeyondLimit()
    {
        while (_instances.Count > MaxInstances)
        {
            var oldest = _instances.MinBy(p => p.Value.LastReceivedUtc).Key;
            _instances.Remove(oldest);
        }
    }

    private sealed class Held(string name, LogRing ring)
    {
        public string Name { get; } = name;

        public LogRing Ring { get; } = ring;

        public DateTimeOffset LastReceivedUtc { get; set; }
    }

    private sealed class HeldSource(Held held, TimeProvider time) : ILogSource
    {
        public string Name => held.Name;

        public ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken)
        {
            var page = after is null ? held.Ring.ReadTail(max, filter) : held.Ring.ReadAfter(after.InstanceId, after.Seq, max, filter);
            var silentFor = time.GetUtcNow() - held.LastReceivedUtc;
            var warning = silentFor > StaleAfter
                ? "The last log batch from this Worker arrived " + (int)silentFor.TotalSeconds + "s ago. If it should be busy, check RabbitMQ and the Worker."
                : null;
            return ValueTask.FromResult(LogSourceResult.Of(held.Name, page) with { Warning = warning });
        }
    }

    private sealed class UnavailableSource(string name, string reason) : ILogSource
    {
        public string Name => name;

        public ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LogSourceResult.Unavailable(name, reason));
    }
}
