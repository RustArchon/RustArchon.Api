// Copyright ©2026 Scott Blomfield

using System.Globalization;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Repositories;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.DiagnosticLogs;

/// <summary>
/// The log of one game server's RustArchon plugin, read on demand (ADR-0004's RCON pull, ADR-0006): each page is an <c>archon.logs.drain</c> sent
/// through the Worker that holds the server's connection, so the plugin does nothing between requests.
/// </summary>
/// <remarks>
/// <para>
/// Every way this can fail to produce a log is named in <see cref="LogSourceResult.UnavailableReason"/> - no such server, a plugin that has
/// never reported, one too old to report the <c>logs</c> capability, a server whose connection is down, no Worker answering, a reply that is not a
/// log - and never returns an empty page that would read as "the plugin had nothing to say".
/// </para>
/// <para>
/// The plugin only drains forward from a cursor, so a first read (no cursor) walks from the start and keeps the most recent matches, and a read
/// with a cursor walks on from it. Either stops after <see cref="MaxRounds"/> round trips; the page then reports how far it got and the caller
/// carries on from there.
/// </para>
/// </remarks>
public sealed class PluginLogSource(
    Guid serverId, IRustServerRepository servers, IServerPluginStatusRepository statuses, IRequestClient<SendRconCommand> rcon) : ILogSource
{
    /// <summary>Entries asked of the plugin per round trip. The plugin may return fewer to keep its reply small.</summary>
    public const int PageSize = 200;

    /// <summary>The most round trips one read makes.</summary>
    public const int MaxRounds = 20;

    private const string LogsCapability = "logs";

    public string Name => "plugin:" + serverId;

    public async ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken)
    {
        var server = await servers.GetByIdAcrossTenantsAsync(serverId);
        if (server is null)
        {
            return Unavailable("There is no server with that id.");
        }

        var status = await statuses.GetForServerAcrossTenantsAsync(server.TenantId, serverId);
        if (status is null)
        {
            return Unavailable("The RustArchon plugin has never reported on this server: it is not installed, or the server has not connected since it was.");
        }

        if (!status.Capabilities.Contains(LogsCapability, StringComparer.Ordinal))
        {
            return Unavailable(
                $"The plugin on this server (version {status.PluginVersion}) does not report the '{LogsCapability}' capability, so it cannot serve logs. Update the plugin.");
        }

        if (server.ConnectionStatus != RconConnectionStatus.Connected)
        {
            return Unavailable($"The server's RCON connection is {server.ConnectionStatus}, so the plugin cannot be asked.");
        }

        var forward = after is not null;
        long bootId = 0;
        long cursor = 0;
        if (after is not null && long.TryParse(after.InstanceId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBoot) && parsedBoot > 0)
        {
            bootId = parsedBoot;
            cursor = after.Seq;
        }

        var matches = new List<LogEntry>();
        var firstRound = true;
        var reset = false;
        long lost = 0;
        long head = 0;
        long oldest = 0;
        long through = cursor;
        var stoppedAtCount = false;

        for (var round = 0; round < MaxRounds && !stoppedAtCount; round++)
        {
            var reply = await AskAsync($"archon.logs.drain {bootId} {cursor} {PageSize}", cancellationToken);
            if (reply.Failure is not null)
            {
                return Unavailable(reply.Failure);
            }

            var drain = reply.Drain!;
            if (firstRound)
            {
                // Only a reader holding a cursor can have lost anything or been reset; a first read just wants the latest.
                reset = forward && drain.Reset;
                lost = forward && !drain.Reset ? Math.Max(0, drain.Oldest - (cursor + 1)) : 0;
                firstRound = false;
            }

            bootId = drain.BootId;
            head = drain.Head;
            oldest = drain.Oldest;

            foreach (var item in drain.Items)
            {
                var entry = new LogEntry(
                    Name, bootId.ToString(CultureInfo.InvariantCulture), item.Seq, item.Time, item.Level, item.Category, item.Message, item.Exception,
                    item.Properties);
                if (!filter(entry))
                {
                    continue;
                }

                matches.Add(entry);
                if (forward && matches.Count >= max)
                {
                    through = entry.Seq;
                    stoppedAtCount = true;
                    break;
                }
            }

            if (stoppedAtCount)
            {
                break;
            }

            through = drain.Cursor;
            cursor = drain.Cursor;
            if (drain.Cursor >= drain.Head || drain.Items.Count == 0)
            {
                break;
            }
        }

        var entries = forward || matches.Count <= max ? matches : matches.GetRange(matches.Count - max, max);
        var page = new LogPage(entries, bootId.ToString(CultureInfo.InvariantCulture), oldest, head, through, lost, reset);
        return LogSourceResult.Of(Name, page);
    }

    private LogSourceResult Unavailable(string reason) => LogSourceResult.Unavailable(Name, reason);

    private async Task<(PluginLogDrain? Drain, string? Failure)> AskAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var response = await rcon.GetResponse<RconCommandResult>(
                new SendRconCommand(serverId, command, Interactive: false), cancellationToken, RequestTimeout.After(s: 10));
            if (!response.Message.Success)
            {
                return (null, $"The command could not be delivered to the server: {response.Message.Error ?? "no reason given"}.");
            }

            return PluginLogReplyParser.TryParse(response.Message.Message, out var drain, out var failure)
                ? (drain, null)
                : (null, $"The plugin did not answer with a log page ({failure}). If its version is old, update it.");
        }
        catch (RequestTimeoutException)
        {
            return (null, "No Worker answered within 10 seconds: the server is not connected to any Worker, or RabbitMQ is not delivering messages.");
        }
    }
}

/// <summary>One page of a plugin's log, as the plugin sent it.</summary>
public sealed record PluginLogDrain(long BootId, long Head, long Oldest, long Cursor, bool Reset, IReadOnlyList<PluginLogItem> Items);

public sealed record PluginLogItem(
    long Seq, DateTimeOffset Time, LogLevel Level, string Category, string Message, string? Exception, IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Parses the plugin's <c>archon.logs.drain</c> reply: the same envelope and cursor contract as its other drains (<c>bootId</c>, <c>head</c>,
/// <c>cursor</c>, <c>reset</c>) plus <c>oldest</c>, with items under <c>logs</c> as <c>{s, t, l, c, m, x?, p?}</c>. Strict about the fields acted on,
/// lenient about extras.
/// </summary>
public static class PluginLogReplyParser
{
    public static bool TryParse(string? message, out PluginLogDrain? drain, out string? failure)
    {
        drain = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(message))
        {
            failure = "empty reply";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            {
                failure = "unsupported or missing envelope version";
                return false;
            }

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var reason = root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : "no error given";
                failure = $"the plugin reported an error: {reason}";
                return false;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !TryLong(data, "format", out var format) || format != 1
                || !TryLong(data, "bootId", out var bootId) || bootId < 1
                || !TryLong(data, "head", out var head) || head < 0
                || !TryLong(data, "oldest", out var oldest) || oldest < 0
                || !TryLong(data, "cursor", out var cursor) || cursor < 0
                || !data.TryGetProperty("reset", out var reset) || reset.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !data.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
            {
                failure = "missing or mistyped format, bootId, head, oldest, cursor, reset or logs";
                return false;
            }

            var items = new List<PluginLogItem>();
            long previous = 0;
            foreach (var item in logs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !TryLong(item, "s", out var seq) || seq <= previous)
                {
                    failure = "a log entry has no valid, increasing sequence number";
                    return false;
                }

                previous = seq;
                var time = item.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime)
                    ? parsedTime
                    : DateTimeOffset.UnixEpoch;
                var level = TryLong(item, "l", out var l) && l is >= 0 and <= 5 ? (LogLevel)l : LogLevel.Information;
                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                if (item.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in p.EnumerateObject())
                    {
                        properties[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
                    }
                }

                items.Add(new PluginLogItem(
                    seq, time, level, Text(item, "c"), Text(item, "m"), item.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null, properties));
            }

            if (items.Count > 0 && items[^1].Seq > cursor)
            {
                failure = "the cursor is behind the last log entry";
                return false;
            }

            drain = new PluginLogDrain(bootId, head, oldest, cursor, reset.ValueKind == JsonValueKind.True, items);
            return true;
        }
        catch (JsonException)
        {
            failure = "the reply is not valid JSON";
            return false;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }
}
