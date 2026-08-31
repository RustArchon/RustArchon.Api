// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A death event (a player or NPC killing a player or NPC) involving at least one player, extracted
/// from an unsolicited console line - see <c>RustArchon.Rcon.KillFeed.KillFeedTextParser</c> and
/// <c>RustArchon.Messaging.Contracts.PlayerKilled</c>'s remarks for why this is heuristic, not
/// authoritative.
/// </summary>
/// <remarks>
/// Derives from <see cref="Entity"/>, not an auditable variant - there is no acting user for a
/// system-detected kill, and this is append-only (no update path at all). <see cref="VictimSteamId"/>/
/// <see cref="KillerSteamId"/> are null when that party is an NPC rather than a player;
/// <see cref="KillerName"/>/<see cref="KillerSteamId"/> are both null when the source line didn't
/// identify a killer at all (environmental/unspecified death). <see cref="RawMessage"/> is always kept
/// so a misparse is auditable rather than silently trusted.
/// </remarks>
[Table("PlayerKillEvent")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(OccurredAtUtc),
    IsDescending = new[] { false, false, true },
    Name = "IX_PlayerKillEvent_TenantId_RustServerId_OccurredAtUtc")]
public class PlayerKillEvent : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string VictimName { get; set; } = string.Empty;
    public string? VictimSteamId { get; set; }

    public string? KillerName { get; set; }
    public string? KillerSteamId { get; set; }

    /// <summary>
    /// The weapon used, when the source line identifies one - or, for a no-killer death line, the
    /// parenthetical cause it reported (e.g. "Suicide", "Bleeding") for lack of a more specific field.
    /// </summary>
    public string? Weapon { get; set; }

    public string RawMessage { get; set; } = string.Empty;
}
