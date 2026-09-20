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

/// <summary>
/// Reads, compresses and expands the plugin's tool cupboard list (format 1). What arrives came from a plugin on someone
/// else's server, so nothing is trusted: a list that is not exactly the shape expected is refused whole, sizes are
/// capped, ids must look like SteamID64s, and names are cut to a sane length.
/// </summary>
public static class TcSnapshotCodec
{
    public const int SupportedFormat = 1;
    public const int MaxCupboards = 20000;
    public const int MaxAuthorizedPerCupboard = 500;
    public const int MaxDecompressedBytes = 32 * 1024 * 1024;
    private const int MaxNameLength = 100;

    /// <summary>A refused list. The message says what was wrong, for a log.</summary>
    public sealed class InvalidTcSnapshotException(string message) : Exception(message);

    /// <summary>Checks a list and returns its cupboards. Throws <see cref="InvalidTcSnapshotException"/> if anything is off.</summary>
    public static List<BaseTcDto> Parse(string tcsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(tcsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidTcSnapshotException("not valid JSON: " + ex.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidTcSnapshotException("not an array");
            }

            if (document.RootElement.GetArrayLength() > MaxCupboards)
            {
                throw new InvalidTcSnapshotException("too many cupboards");
            }

            return document.RootElement.EnumerateArray().Select(ToDto).ToList();
        }
    }

    public static byte[] Compress(List<BaseTcDto> tcs)
    {
        // Stored in the plugin's own compact shape so the format version on the row means what it says.
        var json = JsonSerializer.Serialize(tcs.Select(t => new
        {
            i = t.Id, x = t.X, y = t.Y, z = t.Z, o = t.OwnerId,
            a = t.Authorized.Select(a => new { i = a.PlayerId, n = a.Name })
        }));

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    public static List<BaseTcDto> Decode(byte[] data, int format)
    {
        if (format != SupportedFormat)
        {
            throw new InvalidOperationException($"Tool cupboard snapshot format {format} is not supported by this build.");
        }

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
                throw new InvalidOperationException("A tool cupboard snapshot expanded past the size limit.");
            }
        }

        return Parse(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static BaseTcDto ToDto(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("i", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var idValue)
            || !TryDouble(e, "x", out var x) || !TryDouble(e, "y", out var y) || !TryDouble(e, "z", out var z))
        {
            throw new InvalidTcSnapshotException("a cupboard has no valid id or position");
        }

        var owner = SteamId(e, "o") ?? throw new InvalidTcSnapshotException("a cupboard has no valid owner");
        var dto = new BaseTcDto { Id = idValue, X = x, Y = y, Z = z, OwnerId = owner };

        if (e.TryGetProperty("a", out var authorized))
        {
            if (authorized.ValueKind != JsonValueKind.Array || authorized.GetArrayLength() > MaxAuthorizedPerCupboard)
            {
                throw new InvalidTcSnapshotException("a cupboard's authorized list is not a reasonable array");
            }

            foreach (var a in authorized.EnumerateArray())
            {
                var playerId = a.ValueKind == JsonValueKind.Object ? SteamId(a, "i") : null;
                if (playerId is null)
                {
                    throw new InvalidTcSnapshotException("an authorized player has no valid id");
                }

                var name = a.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                dto.Authorized.Add(new BaseAuthorizedPlayerDto { PlayerId = playerId, Name = name.Length <= MaxNameLength ? name : name[..MaxNameLength] });
            }
        }

        return dto;
    }

    // A SteamID64: digits only, and no longer than the largest ulong.
    private static string? SteamId(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString() ?? "";
        return value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit) ? value : null;
    }

    private static bool TryDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value) && double.IsFinite(value);
    }
}
