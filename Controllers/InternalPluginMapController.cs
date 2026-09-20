// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Messaging.Contracts;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The Api side of the Panel's public map upload door: redeems a single-use token and, if it was good, stores the picture the
/// game server sends. Not reachable from outside - authenticated by the shared internal-service key, called only by the
/// Panel's anonymous <c>/ingest/plugin-map</c> route (the token arrives in the <c>X-RustArchon-Upload-Token</c> header and is the only
/// credential: it names the server and the map it was minted for), which is what a game server's plugin actually contacts.
/// </summary>
/// <remarks>
/// Nothing unauthenticated ever reaches storage: a bad token is a bare 404 before the body is read, and a good one is still
/// held to a size ceiling and to being a PNG, so a stolen or misused token can store only one picture-shaped file for the one
/// world it was minted for. Every refusal that happens before the token is checked is the same 404, so a guesser learns nothing.
/// </remarks>
[ApiController]
[Route("internal/plugin/map")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalPluginMapController(
    IPluginMapUploadTokenRepository tokens,
    IPluginMapRepository maps,
    IObjectStorage storage,
    IMapPreviewService previews,
    TimeProvider clock,
    ILogger<InternalPluginMapController> logger) : ControllerBase
{
    /// <summary>The longest token this will even look up. A real one is 43 characters.</summary>
    public const int MaxTokenLength = 128;

    /// <summary>The largest picture accepted. A 4500 m map is about 24 MB; the ceiling leaves room for the game's biggest worlds.</summary>
    public const long MaxBytes = 120L * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [HttpPost]
    [RequestSizeLimit(MaxBytes)]
    public async Task<IActionResult> Upload([FromHeader(Name = RustArchonPlugin.MapUploadTokenHeader)] string? token)
    {
        Response.Headers.CacheControl = "no-store";

        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return NotFound();
        }

        // Redeemed first, so an unauthenticated caller cannot make this read a byte of body.
        var redemption = await tokens.RedeemAsync(token);
        if (redemption is null)
        {
            return NotFound();
        }

        // The token names both the server and the map row; the row must agree that it belongs to that server.
        var map = await maps.GetByIdAsync(redemption.PluginMapId);
        if (map is null || map.RustServerId != redemption.RustServerId)
        {
            return NotFound();
        }

        var serverId = map.RustServerId;

        var declared = Request.ContentLength;
        if (declared is null or <= 0 or > MaxBytes)
        {
            return BadRequest("A picture of a known size, no larger than the limit, is required.");
        }

        using var buffer = new MemoryStream((int)declared.Value);
        using var sha = SHA256.Create();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await Request.Body.ReadAsync(chunk)) > 0)
        {
            total += read;
            if (total > declared.Value || total > MaxBytes)
            {
                return BadRequest("The body is larger than it said.");
            }

            buffer.Write(chunk, 0, read);
            sha.TransformBlock(chunk, 0, read, null, 0);
        }

        if (total != declared.Value)
        {
            return BadRequest("The body ended early.");
        }

        sha.TransformFinalBlock([], 0, 0);
        var bytes = buffer.ToArray();
        if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            logger.LogWarning("Server {ServerId} sent a map upload that is not a PNG; refused.", serverId);
            return BadRequest("Not a PNG.");
        }

        // A few hundred bytes can claim to be 60,000 pixels across; the declared size is read from the header and held to a
        // ceiling before anything is stored or decoded.
        if (!PngHeader.TryReadSize(bytes, out var pictureWidth, out var pictureHeight))
        {
            logger.LogWarning("Server {ServerId} sent a map upload with no readable PNG header; refused.", serverId);
            return BadRequest("Not a PNG.");
        }

        if (!PngHeader.IsAcceptableMapSize(pictureWidth, pictureHeight))
        {
            logger.LogWarning("Server {ServerId} sent a {Width} x {Height} map picture; the limit is {Max} pixels a side. Refused.", serverId, pictureWidth, pictureHeight, PngHeader.MaxMapSide);
            return BadRequest("The picture is larger than the limit.");
        }

        var key = $"maps/{map.RustServerId}/{map.WorldSize}_{map.WorldSeed}.png";
        await storage.PutAsync(key, bytes, "image/png");
        await maps.RecordUploadAsync(map.Id, bytes.Length, Convert.ToHexStringLower(sha.Hash!), key, clock.GetUtcNow());

        // The display-sized copy. A failure to make it must not fail the upload: the original is safely stored and the map is
        // then served from it (slowly), or the preview is made on first request.
        try
        {
            await previews.CreateAsync(map, bytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storing the preview of the map for server {ServerId} failed; the original is stored.", serverId);
        }

        logger.LogInformation("Stored the {Size} m map for server {ServerId} ({Bytes} bytes).", map.WorldSize, serverId, bytes.Length);
        return NoContent();
    }
}
