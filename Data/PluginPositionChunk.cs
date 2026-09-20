// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One batch of player position samples drained from a server's RustArchon plugin, stored as a single compressed row
/// rather than a row per sample (a busy server makes about a million a day). Read back in time windows or for one player.
/// See <c>docs/plans/companion-server-plugin.md</c> (Phase 2, positions).
/// </summary>
/// <remarks>
/// The same shape and the same reasons as <see cref="PluginCombatChunk"/>: <see cref="Data"/> is the plugin's own JSON
/// array, gzipped and versioned by <see cref="Format"/>; (<see cref="BootId"/>, sequence) makes a batch that arrives twice
/// drop out instead of being stored twice; <see cref="PlayerIds"/> lists who is in the batch so a per-player view does not
/// have to open every chunk.
/// </remarks>
[Table("PluginPositionChunk")]
[Index(nameof(RustServerId), nameof(ToUtc), Name = "IX_PluginPositionChunk_Server_ToUtc")]
[Index(nameof(RustServerId), nameof(BootId), nameof(FirstSequence), IsUnique = true, Name = "IX_PluginPositionChunk_Server_Boot_FirstSequence")]
[Index(nameof(ToUtc), Name = "IX_PluginPositionChunk_ToUtc")]
public class PluginPositionChunk : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>Identifies one run of the plugin; its sequence numbers restart every run.</summary>
    public long BootId { get; set; }

    public long FirstSequence { get; set; }
    public long LastSequence { get; set; }

    public int SampleCount { get; set; }

    /// <summary>The earliest and latest sample time in the batch.</summary>
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }

    /// <summary>The plugin's sample format the data is in. Only 1 exists.</summary>
    public int Format { get; set; } = 1;

    /// <summary>The samples, a JSON array in the plugin's compact format, gzipped.</summary>
    [Required]
    public byte[] Data { get; set; } = [];

    /// <summary>SteamIDs of the players who appear in this batch.</summary>
    public string[] PlayerIds { get; set; } = [];

    /// <summary>True when samples before this batch were missed (the plugin's buffer wrapped, or it reloaded): a gap, not data.</summary>
    public bool PrecededByGap { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
