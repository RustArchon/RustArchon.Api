// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One batch of combat events drained from a server's RustArchon plugin, stored as a single compressed row rather than
/// a row per hit: a busy PvP server can produce millions of hits a day, and the Panel only ever reads them back in time
/// windows or for one player. See <c>docs/plans/companion-server-plugin.md</c> (Phase 2, combat log).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Data"/> is the plugin's own JSON array (its compact format, versioned by <see cref="Format"/>), gzipped.
/// Keeping the format version on the row means the layout can change once real volumes are known without touching
/// what is already stored. <see cref="PlayerIds"/> lists the real players in the batch (as attacker or victim) so a
/// per-player view does not have to open every chunk.
/// </para>
/// <para>
/// The plugin numbers its events per run (<see cref="BootId"/>): (<see cref="BootId"/>, sequence) is what makes a batch
/// the Worker sends twice, or sends again after a restart, drop out instead of being stored twice.
/// </para>
/// </remarks>
[Table("PluginCombatChunk")]
[Index(nameof(RustServerId), nameof(ToUtc), Name = "IX_PluginCombatChunk_Server_ToUtc")]
[Index(nameof(RustServerId), nameof(BootId), nameof(FirstSequence), IsUnique = true, Name = "IX_PluginCombatChunk_Server_Boot_FirstSequence")]
[Index(nameof(ToUtc), Name = "IX_PluginCombatChunk_ToUtc")]
public class PluginCombatChunk : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>Identifies one run of the plugin; its sequence numbers restart every run.</summary>
    public long BootId { get; set; }

    public long FirstSequence { get; set; }
    public long LastSequence { get; set; }

    public int EventCount { get; set; }

    /// <summary>The earliest and latest event time in the batch.</summary>
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }

    /// <summary>The plugin's event format the data is in. Only 1 exists.</summary>
    public int Format { get; set; } = 1;

    /// <summary>The events, a JSON array in the plugin's compact format, gzipped.</summary>
    [Required]
    public byte[] Data { get; set; } = [];

    /// <summary>SteamIDs of the real players who appear in this batch.</summary>
    public string[] PlayerIds { get; set; } = [];

    /// <summary>True when events before this batch were missed (the plugin's buffer wrapped, or it reloaded): a gap, not data.</summary>
    public bool PrecededByGap { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
