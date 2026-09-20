// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Repositories;
using SkiaSharp;

namespace RustArchon.Api.Infrastructure;

/// <summary>Makes the small copy of a map picture that the Panel actually shows.</summary>
public interface IMapPreviewRenderer
{
    /// <summary>
    /// A WebP no wider or taller than <see cref="MapPreviewRenderer.MaxSide"/>, or null when the bytes are not an image this can
    /// read (a file that starts like a PNG and is not one). Never throws for bad input.
    /// </summary>
    byte[]? Render(byte[] picture);
}

/// <inheritdoc cref="IMapPreviewRenderer" />
/// <remarks>
/// A 4500 m map is a 5500 x 5500 picture of 24 MB. Decoding it takes a few hundred megabytes of memory for a moment, so this runs
/// once per picture (when it arrives), never per view. The preview is never scaled UP: a smaller picture is re-encoded as it is.
/// </remarks>
public class MapPreviewRenderer : IMapPreviewRenderer
{
    /// <summary>
    /// The longest side of the preview, in pixels. The game's own picture is one pixel per metre (5500 px for a 4500 m world), so this
    /// keeps it at full resolution for every normal world and only scales down the very largest. As lossy WebP that is about 0.05 bytes per
    /// pixel: a 30-megapixel picture of 24 MB as a lossless PNG is roughly 1.6 MB (measured on the test world: WebP came out about 30%
    /// smaller than JPEG at the same setting, and keeps the hard edges - roads, coastlines, outlines - cleaner). The cost is in the browser, not the network: a picture
    /// this size takes about 120 MB once decoded, which a desktop takes easily and a small phone may not (the reason it was 4096 first).
    /// </summary>
    public const int MaxSide = 6144;

    // 80 in WebP looks like about 85 in JPEG.
    private const int WebpQuality = 80;

    public byte[]? Render(byte[] picture)
    {
        try
        {
            // Strict: the whole picture must decode. Skia will happily render the top half of a truncated PNG, and a half-drawn
            // map must not be presented as the map.
            using var codec = SKCodec.Create(new SKMemoryStream(picture));
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            using var decoded = new SKBitmap(codec.Info);
            if (codec.GetPixels(codec.Info, decoded.GetPixels()) != SKCodecResult.Success)
            {
                return null;
            }

            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(decoded.Width, decoded.Height));
            var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));

            using var resized = scale < 1.0
                ? decoded.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
                : decoded.Copy();
            if (resized is null)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
            return encoded?.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Makes, stores and records the preview of a map's picture.</summary>
public interface IMapPreviewService
{
    /// <summary>
    /// Makes the preview from <paramref name="picture"/> (the original's bytes, already in hand) and records it. False when it
    /// could not be made; the map is then served from the original.
    /// </summary>
    Task<bool> CreateAsync(PluginMap map, byte[] picture);

    /// <summary>
    /// Makes the preview for a map that has a stored picture but no preview yet (one collected before previews existed).
    /// Returns the map as it now stands: with a preview if one could be made, otherwise unchanged.
    /// </summary>
    Task<PluginMap> EnsureAsync(PluginMap map);
}

/// <inheritdoc cref="IMapPreviewService" />
public class MapPreviewService(
    IMapPreviewRenderer renderer, IObjectStorage storage, IPluginMapRepository maps, ILogger<MapPreviewService> logger) : IMapPreviewService
{
    public async Task<bool> CreateAsync(PluginMap map, byte[] picture)
    {
        var webp = renderer.Render(picture);
        if (webp is null)
        {
            logger.LogWarning("Could not make a preview of the {Size} m map for server {ServerId}; it will be served from the original.", map.WorldSize, map.RustServerId);
            return false;
        }

        var key = PreviewKey(map);
        await storage.PutAsync(key, webp, "image/webp");
        await maps.RecordPreviewAsync(map.Id, key, webp.Length, Convert.ToHexStringLower(SHA256.HashData(webp)));

        // A preview made at an older size is now orphaned; tidy it away (best effort - it costs only space).
        if (map.PreviewObjectKey is not null && map.PreviewObjectKey != key)
        {
            try
            {
                await storage.DeleteAsync(map.PreviewObjectKey);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not remove the old preview {Key}.", map.PreviewObjectKey);
            }
        }

        return true;
    }

    /// <summary>
    /// Where a map's preview lives. The size is in the name, so a preview made at an older size (or by an older renderer setting)
    /// (or in another format) is recognised as stale by its key alone and made again.
    /// </summary>
    public static string PreviewKey(PluginMap map) =>
        $"maps/{map.RustServerId}/{map.WorldSize}_{map.WorldSeed}.preview{MapPreviewRenderer.MaxSide}.webp";

    public async Task<PluginMap> EnsureAsync(PluginMap map)
    {
        if (map.ObjectKey is null || map.PreviewObjectKey == PreviewKey(map))
        {
            return map;
        }

        var original = await storage.GetAsync(map.ObjectKey);
        if (original is null || !await CreateAsync(map, original.Content))
        {
            return map;
        }

        return await maps.GetByIdAsync(map.Id) ?? map;
    }
}
