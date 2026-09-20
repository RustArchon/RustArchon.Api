// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// What is known about one server's world map for one wipe: the world (size and seed identify a wipe), whether the game
/// has drawn a picture of it, where the collected copy is stored, and the named places on it. One row per server per
/// world, so a wipe adds a row and the previous wipe's picture stays available.
/// </summary>
/// <remarks>
/// The picture itself lives in object storage (<see cref="ObjectKey"/>); it is tens of megabytes, which does not belong in
/// Postgres. The row is created when the Worker first reports the world and is completed when the picture arrives.
/// </remarks>
[Table("PluginMap")]
[Index(nameof(RustServerId), nameof(WorldSize), nameof(WorldSeed), IsUnique = true, Name = "IX_PluginMap_Server_World")]
public class PluginMap : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The world's size in metres (it is square).</summary>
    public int WorldSize { get; set; }

    public long WorldSeed { get; set; }

    /// <summary>The picture's file name on the game server, <c>map_{size}_{seed}.png</c>.</summary>
    [MaxLength(100)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Whether the game server had the picture when it was last asked, and how big it said it was.</summary>
    public bool ExistsOnServer { get; set; }

    public long ServerBytes { get; set; }

    /// <summary>The named places, the plugin's <c>monuments</c> array as JSON. Null until first reported.</summary>
    public string? MonumentsJson { get; set; }

    /// <summary>The last time the Worker reported this world.</summary>
    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>When the Api last asked the server to upload; the throttle that stops a failing upload being retried every poll.</summary>
    public DateTimeOffset? UploadRequestedAtUtc { get; set; }

    /// <summary>When the picture arrived, and what arrived. Null until it has.</summary>
    public DateTimeOffset? UploadedAtUtc { get; set; }
    public long? UploadedBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the stored picture.</summary>
    [MaxLength(64)]
    public string? Sha256 { get; set; }

    [MaxLength(200)]
    public string? ObjectKey { get; set; }

    /// <summary>
    /// A display-sized copy of the picture (a WebP at the picture's own resolution, about 1.6 MB against 24 MB for the PNG), which is what the Panel shows: the original
    /// is 24 MB or more, far too much to send to a browser to draw on a canvas a thousand pixels wide. The original stays in storage.
    /// Null until made (it is made when the picture arrives, or on first request for a picture that arrived before this existed).
    /// </summary>
    [MaxLength(200)]
    public string? PreviewObjectKey { get; set; }

    public long? PreviewBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the preview: its entity tag.</summary>
    [MaxLength(64)]
    public string? PreviewSha256 { get; set; }
}
