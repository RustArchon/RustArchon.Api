// Copyright ©2026 Scott Blomfield

using System;
using System.Buffers.Binary;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Reads the size a PNG declares, from its first 24 bytes, without decoding anything. A PNG can be a few hundred bytes and still
/// claim to be 60,000 x 60,000 pixels; decoding that would ask for gigabytes. The size is checked here, first, so it never gets that far.
/// </summary>
public static class PngHeader
{
    /// <summary>The bytes the size lives in: the 8-byte signature, then the IHDR chunk's length, name, width and height.</summary>
    public const int SizeBytes = 24;

    /// <summary>
    /// The longest side a map picture may have, in pixels. The game's largest world (6000 m) is 7000 pixels across with its ocean
    /// margin; this leaves room above that and stops well short of an allocation that could hurt the server.
    /// </summary>
    public const int MaxMapSide = 8192;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>True when <paramref name="bytes"/> starts with the PNG signature and an IHDR chunk; gives the size it declares.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> bytes, out long width, out long height)
    {
        width = height = 0;
        if (bytes.Length < SizeBytes || !bytes[..Signature.Length].SequenceEqual(Signature))
        {
            return false;
        }

        // Chunk layout: 4-byte length, 4-byte name, data. IHDR is always first and its data starts with width then height.
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        return true;
    }

    /// <summary>True when the declared size is a real one (not zero) and within <see cref="MaxMapSide"/> on both sides.</summary>
    public static bool IsAcceptableMapSize(long width, long height) =>
        width > 0 && height > 0 && width <= MaxMapSide && height <= MaxMapSide;
}
