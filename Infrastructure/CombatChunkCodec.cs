// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure;

/// <summary>One event as it arrived from the plugin: its sequence, its time, who was involved, and its own JSON, untouched.</summary>
public sealed record ParsedCombatEvent(long Sequence, DateTimeOffset OccurredAtUtc, string? AttackerPlayerId, string? VictimPlayerId, string RawJson);

/// <summary>
/// Reads, compresses and expands the plugin's combat events (format 1). Everything that arrives here came from a plugin
/// running on someone else's server, so nothing is trusted: a batch that is not the exact shape expected is refused
/// whole, sizes are capped, and text fields are cut to a sane length before they are ever shown.
/// </summary>
public static class CombatChunkCodec
{
    public const int SupportedFormat = 1;

    /// <summary>The most events one batch may hold (the Worker asks the plugin for at most 500).</summary>
    public const int MaxEventsPerBatch = 1000;

    /// <summary>The most a chunk may expand to when read back. Far above any real batch; stops a crafted one being a bomb.</summary>
    public const int MaxDecompressedBytes = 8 * 1024 * 1024;

    private const int MaxTextLength = 100;

    /// <summary>A refused batch. The message says what was wrong, for a log.</summary>
    public sealed class InvalidCombatBatchException(string message) : Exception(message);

    /// <exception cref="InvalidCombatBatchException">The batch is not a well-formed array of format-1 events.</exception>
    public static IReadOnlyList<ParsedCombatEvent> Parse(string eventsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(eventsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidCombatBatchException("not valid JSON: " + ex.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidCombatBatchException("not an array");
            }

            if (document.RootElement.GetArrayLength() > MaxEventsPerBatch)
            {
                throw new InvalidCombatBatchException("too many events");
            }

            var events = new List<ParsedCombatEvent>();
            long previous = 0;
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !TryLong(e, "s", out var sequence) || sequence < 1
                    || !TryLong(e, "t", out var unixMs) || unixMs < 0 || unixMs > 253402300799000L)
                {
                    throw new InvalidCombatBatchException("an event has no valid sequence or time");
                }

                if (sequence <= previous)
                {
                    throw new InvalidCombatBatchException("events are not in increasing order");
                }

                previous = sequence;
                events.Add(new ParsedCombatEvent(
                    sequence,
                    DateTimeOffset.FromUnixTimeMilliseconds(unixMs),
                    PlayerIdOf(e, "ap", "a"),
                    PlayerIdOf(e, "vp", "v"),
                    e.GetRawText()));
            }

            return events;
        }
    }

    /// <summary>The events' JSON array (built from each event's own text, in order), gzipped.</summary>
    public static byte[] Compress(IEnumerable<ParsedCombatEvent> events)
    {
        var json = "[" + string.Join(",", events.Select(e => e.RawJson)) + "]";
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>The events in a stored chunk, as the Panel shows them.</summary>
    public static List<CombatEventDto> Decode(byte[] data, int format)
    {
        if (format != SupportedFormat)
        {
            throw new InvalidOperationException($"Combat chunk format {format} is not supported by this build.");
        }

        var json = Decompress(data);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(ToDto).ToList();
    }

    private static string Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaxDecompressedBytes)
            {
                throw new InvalidOperationException("A combat chunk expanded past the size limit.");
            }
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static CombatEventDto ToDto(JsonElement e)
    {
        var attackerIsPlayer = Bool(e, "ap");
        var victimIsPlayer = Bool(e, "vp");
        return new CombatEventDto
        {
            Sequence = Long(e, "s"),
            OccurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(Long(e, "t")),
            Kind = Text(e, "k") is "death" ? "death" : "hit",
            AttackerId = Text(e, "a"),
            AttackerName = Text(e, "an"),
            AttackerIsPlayer = attackerIsPlayer,
            VictimId = Text(e, "v"),
            VictimName = Text(e, "vn"),
            VictimIsPlayer = victimIsPlayer,
            Weapon = Text(e, "w"),
            Damage = Number(e, "d") ?? 0,
            DamageType = Text(e, "dt"),
            Headshot = Bool(e, "hs"),
            Distance = Number(e, "dist"),
            AttackerPosition = Position(e, "apos"),
            VictimPosition = Position(e, "vpos")
        };
    }

    // A player id is only taken when the event says the party is a player AND it looks like a SteamID64 (digits only).
    // Anything else is treated as an entity name, never as a player, so a crafted event cannot put arbitrary text into
    // the per-player index.
    private static string? PlayerIdOf(JsonElement e, string flag, string id)
    {
        if (!Bool(e, flag))
        {
            return null;
        }

        var value = Text(e, id);
        return value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit) ? value : null;
    }

    private static bool TryLong(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static long Long(JsonElement e, string name) => TryLong(e, name, out var v) ? v : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = element.GetString() ?? string.Empty;
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            && double.IsFinite(value) ? value : null;

    private static double[]? Position(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
        {
            return null;
        }

        var result = new double[3];
        var i = 0;
        foreach (var part in element.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetDouble(out result[i]) || !double.IsFinite(result[i]))
            {
                return null;
            }

            i++;
        }

        return result;
    }
}
