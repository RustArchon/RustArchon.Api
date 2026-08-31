// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One player's connect-to-disconnect session on a registered server, including the IP address they
/// connected from and (once looked up) where that IP appears to be and whether it looks like a
/// VPN/proxy.
/// </summary>
/// <remarks>
/// Derives from <see cref="Entity"/>, not an auditable variant - there is no acting user for a
/// system-detected connect/disconnect, and no legitimate update path beyond the specific fields this
/// class itself manages (<see cref="DisconnectedAtUtc"/>, the geolocation columns). A currently-open
/// session is one whose <see cref="DisconnectedAtUtc"/> is still <c>null</c> - see
/// <c>IPlayerSessionRepository.GetCurrentlyConnectedAsync</c>.
/// </remarks>
[Table("PlayerSession")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(SteamId), nameof(DisconnectedAtUtc),
    Name = "IX_PlayerSession_TenantId_RustServerId_SteamId_DisconnectedAtUtc")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(ConnectedAtUtc),
    IsDescending = new[] { false, false, true },
    Name = "IX_PlayerSession_TenantId_RustServerId_ConnectedAtUtc")]
public class PlayerSession : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The player's Steam64 ID - a stable identity across sessions, unlike display name.</summary>
    public string SteamId { get; set; } = string.Empty;

    /// <summary>The display name at connect time - not kept in sync if the player renames mid-session.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;

    public DateTimeOffset ConnectedAtUtc { get; set; }

    /// <summary>Null while the session is still open (the player is currently connected).</summary>
    public DateTimeOffset? DisconnectedAtUtc { get; set; }

    // Geolocation - all null until (if ever) IGeolocationService successfully resolves this session's
    // IpAddress. Every provider is currently a stub (see Infrastructure/Geolocation), so in practice
    // these stay null for now - the columns exist so filling in a real provider is a pure behavior
    // change, not a schema change.
    public string? GeolocationProvider { get; set; }
    public string? GeolocationCountry { get; set; }
    public string? GeolocationCountryCode { get; set; }
    public bool? GeolocationIsVpn { get; set; }
    public bool? GeolocationIsProxy { get; set; }
    public DateTimeOffset? GeolocationCheckedAtUtc { get; set; }

    // Live stats - only ever available from a playerlist poll snapshot (never from the instant
    // console-text connect detection), so these start null and are filled in by the first
    // reconciliation poll after connect (see PlayerSessionSnapshotUpdatedConsumer), then keep
    // updating on every subsequent poll for as long as the session stays open. Deliberately left as
    // whatever they last were once the session closes ("last known"), not cleared - that's the whole
    // point for the inactive-players view.
    public int? LastPing { get; set; }
    public decimal? LastViolationLevel { get; set; }

    // Steam - all null until (if ever) ISteamApiClient successfully resolves this session's SteamId,
    // which only happens when the owning RustServer has a SteamApiKey configured. Looked up once at
    // connect time, same timing as geolocation above - not re-checked later in the session, so a ban
    // that lands mid-session won't retroactively update an already-open session's columns.
    public bool? SteamVacBanned { get; set; }
    public int? SteamNumberOfVacBans { get; set; }
    public int? SteamNumberOfGameBans { get; set; }

    /// <summary>Total playtime in Rust specifically, per Steam's own records, in minutes - null if
    /// never looked up, the lookup failed, or the profile's game details aren't public.</summary>
    public int? SteamMinutesPlayedForever { get; set; }

    public DateTimeOffset? SteamInfoCheckedAtUtc { get; set; }
}
