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

/// <summary>One sample as it arrived from the plugin: its sequence, time, player, and its own JSON, untouched.</summary>
public sealed record ParsedPositionSample(long Sequence, DateTimeOffset OccurredAtUtc, string PlayerId, string RawJson);

/// <summary>
/// Reads, compresses and expands the plugin's position samples (format 1). Everything that arrives here came from a plugin
/// running on someone else's server, so nothing is trusted: a batch that is not the exact shape expected is refused whole,
/// every sample must carry a real SteamID64 and finite coordinates, sizes are capped, and text is cut before it is shown.
/// </summary>
public static class PositionChunkCodec
{
    public const int SupportedFormat = 1;

    /// <summary>The most samples one batch may hold (the Worker asks the plugin for at most 500).</summary>
    public const int MaxSamplesPerBatch = 1000;

    /// <summary>The most a chunk may expand to when read back; stops a crafted one being a bomb.</summary>
    public const int MaxDecompressedBytes = 8 * 1024 * 1024;

    private const int MaxTextLength = 100;

    /// <summary>A refused batch. The message says what was wrong, for a log.</summary>
    public sealed class InvalidPositionBatchException(string message) : Exception(message);

    /// <exception cref="InvalidPositionBatchException">The batch is not a well-formed array of format-1 samples.</exception>
    public static IReadOnlyList<ParsedPositionSample> Parse(string samplesJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(samplesJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidPositionBatchException("not valid JSON: " + ex.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidPositionBatchException("not an array");
            }

            if (document.RootElement.GetArrayLength() > MaxSamplesPerBatch)
            {
                throw new InvalidPositionBatchException("too many samples");
            }

            var samples = new List<ParsedPositionSample>();
            long previous = 0;
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !TryLong(e, "s", out var sequence) || sequence < 1
                    || !TryLong(e, "t", out var unixMs) || unixMs < 0 || unixMs > 253402300799000L)
                {
                    throw new InvalidPositionBatchException("a sample has no valid sequence or time");
                }

                var player = Text(e, "p");
                if (player.Length is 0 or > 20 || !player.All(char.IsAsciiDigit))
                {
                    throw new InvalidPositionBatchException("a sample has no valid player id");
                }

                if (Number(e, "x") is null || Number(e, "y") is null || Number(e, "z") is null)
                {
                    throw new InvalidPositionBatchException("a sample has no valid position");
                }

                if (sequence <= previous)
                {
                    throw new InvalidPositionBatchException("samples are not in increasing order");
                }

                previous = sequence;
                samples.Add(new ParsedPositionSample(sequence, DateTimeOffset.FromUnixTimeMilliseconds(unixMs), player, e.GetRawText()));
            }

            return samples;
        }
    }

    /// <summary>The samples' JSON array (built from each sample's own text, in order), gzipped.</summary>
    public static byte[] Compress(IEnumerable<ParsedPositionSample> samples)
    {
        var json = "[" + string.Join(",", samples.Select(s => s.RawJson)) + "]";
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>The samples in a stored chunk, as the Panel shows them.</summary>
    public static List<PositionSampleDto> Decode(byte[] data, int format)
    {
        if (format != SupportedFormat)
        {
            throw new InvalidOperationException($"Position chunk format {format} is not supported by this build.");
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
                throw new InvalidOperationException("A position chunk expanded past the size limit.");
            }
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static PositionSampleDto ToDto(JsonElement e) => new()
    {
        Sequence = Long(e, "s"),
        OccurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(Long(e, "t")),
        PlayerId = Text(e, "p"),
        PlayerName = Text(e, "n"),
        X = Number(e, "x") ?? 0,
        Y = Number(e, "y") ?? 0,
        Z = Number(e, "z") ?? 0,
        Yaw = (int)Math.Clamp(Number(e, "r") ?? 0, 0, 359),
        Marker = Text(e, "e") is "on" ? "on" : Text(e, "e") is "off" ? "off" : string.Empty
    };

    private static bool TryLong(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static long Long(JsonElement e, string name) => TryLong(e, name, out var v) ? v : 0;

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
}
