// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// A server's world map: what is known about the current world (its size, the named places) and the picture itself, as the
/// RustArchon plugin drew and delivered it. Gated like reading a server (<c>RustServer.Get</c>): the map is the game's own
/// public map, not player data, so it does not need the stricter permissions positions and bases do.
/// </summary>
/// <remarks>
/// Reads through the tenant-filtered repository and the tenant-filtered server lookup, so another organization's server id
/// is a plain 404 - never an empty answer that would confirm the server exists.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/map")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerGet)]
public class ServerMapController(
    IRustServerRepository servers, IPluginMapRepository maps, IObjectStorage storage, IMapPreviewService previews) : ControllerBase
{
    private const int MaxMonuments = 500;
    private const int MaxNameLength = 100;

    [HttpGet]
    public async Task<ActionResult<MapDto>> Get(Guid id)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        var map = await maps.GetCurrentAsync(id);
        if (map is null)
        {
            return Ok(new MapDto());
        }

        return Ok(new MapDto
        {
            Available = map.UploadedAtUtc is not null && map.ObjectKey is not null,
            WorldSize = map.WorldSize,
            WorldSeed = map.WorldSeed,
            UploadedAtUtc = map.UploadedAtUtc,
            // What /image serves: the preview when there is one (which it makes on first request for older pictures), else the original.
            ImageBytes = map.PreviewBytes ?? map.UploadedBytes ?? 0,
            ImageEtag = map.PreviewSha256 ?? map.Sha256,
            Monuments = ParseMonuments(map.MonumentsJson)
        });
    }

    /// <summary>
    /// The picture, as the display-sized WebP the Panel draws (or, with <paramref name="full"/>, the original PNG, which is tens of
    /// megabytes). Cacheable: it is addressed by its content hash, so a redrawn map has a different entity tag.
    /// </summary>
    [HttpGet("image")]
    public async Task<IActionResult> Image(Guid id, [FromQuery] bool full = false)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        var map = await maps.GetCurrentAsync(id);
        if (map?.ObjectKey is null || map.Sha256 is null)
        {
            return NotFound();
        }

        // A picture collected before previews existed gets its preview now, once.
        if (!full)
        {
            map = await previews.EnsureAsync(map);
        }

        var usePreview = !full && map.PreviewObjectKey is not null && map.PreviewSha256 is not null;
        var objectKey = usePreview ? map.PreviewObjectKey! : map.ObjectKey;
        var hash = usePreview ? map.PreviewSha256! : map.Sha256;
        var contentType = usePreview ? "image/webp" : "image/png";

        var etag = $"\"{hash}\"";
        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var stored = await storage.GetAsync(objectKey);
        if (stored is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(stored.Content, contentType);
    }

    // The list came from a plugin on someone else's server: it is re-read defensively, names are cut, and the count is capped.
    private static List<MapMonumentDto> ParseMonuments(string? json)
    {
        var result = new List<MapMonumentDto>();
        if (string.IsNullOrEmpty(json))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var m in document.RootElement.EnumerateArray())
            {
                if (result.Count >= MaxMonuments)
                {
                    break;
                }

                if (m.ValueKind != JsonValueKind.Object
                    || !m.TryGetProperty("n", out var name) || name.ValueKind != JsonValueKind.String
                    || !TryNumber(m, "x", out var x) || !TryNumber(m, "y", out var y) || !TryNumber(m, "z", out var z))
                {
                    continue;
                }

                var text = name.GetString() ?? string.Empty;
                result.Add(new MapMonumentDto { Name = text.Length <= MaxNameLength ? text : text[..MaxNameLength], X = x, Y = y, Z = z });
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }

    private static bool TryNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value) && double.IsFinite(value);
    }
}
