// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.Auditing;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// Represents a Rust game server a tenant has registered for RCON management.
/// </summary>
/// <remarks>
/// Tenant-scoped (<see cref="ITenantScoped"/>) so one tenant's servers are never visible to another
/// - see JumpStart's multi-tenancy documentation. <see cref="RconPassword"/> is always stored
/// encrypted via <see cref="Infrastructure.Security.IRconCredentialProtector"/>, applied in
/// <see cref="Controllers.RustServersController"/>; it is never exposed through the API - see
/// <c>RustArchon.Shared.DTOs.RustServerDto</c>.
/// </remarks>
// The (TenantId, Name) uniqueness constraint lives in ApiDbContext.OnModelCreating, not here as a
// plain [Index] attribute - it has to be a filtered index (WHERE "DeletedOn" IS NULL), and attribute
// indexes can't carry a filter. See that Fluent API configuration's remarks for why: a plain
// unfiltered unique index blocks re-adding a server under a name that only a *soft-deleted* row still
// holds - confirmed live as a real, reproducible bug (delete a server, try to re-add the same name,
// get a raw DbUpdateException/23505 all the way back to the caller).
[Table("RustServer")]
public class RustServer : AuditableNamedEntity, ITenantScoped
{
    /// <summary>
    /// Gets or sets the unique identifier of the tenant that owns this server.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Gets or sets the tenant that owns this server.
    /// </summary>
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Gets or sets the hostname or IP address the RCON connection is made to.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the RCON port. Rust's default RCON port is 28016.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 28016;

    /// <summary>
    /// Gets or sets the RCON password, encrypted at rest. Set only via
    /// <see cref="Infrastructure.Security.IRconCredentialProtector.Protect"/> - never store plaintext here.
    /// </summary>
    /// <remarks>
    /// No explicit <c>[Column(TypeName = ...)]</c> - an unbounded <see cref="string"/> property with
    /// no <c>[MaxLength]</c> already maps to each provider's own "unlimited text" type by convention
    /// (<c>text</c> on PostgreSQL, <c>nvarchar(max)</c> on SQL Server), which stays portable across
    /// providers instead of hard-coding one provider's SQL type name.
    /// </remarks>
    [Required]
    public string RconPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional free-text description of this server.
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets whether this server should have a persistent WebRCON connection at all. Disabling
    /// a server publishes <see cref="ServerLifecycleChangeType.Disabled"/> so whichever
    /// <c>RustArchon.Worker</c> instance owns its connection tears it down; re-enabling publishes a
    /// fresh <see cref="ConnectToServer"/> claim.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the live state of this server's underlying WebRCON socket, as last reported by
    /// <see cref="ConnectionStatusChanged"/>. This is the UI-facing "is it actually connected right
    /// now" signal - distinct from <see cref="AssignedWorkerId"/>/<see cref="LastHeartbeatUtc"/>
    /// below, which answer "is a worker still responsible for this server at all" (a server can have
    /// a very fresh heartbeat while sitting in <see cref="RconConnectionStatus.Reconnecting"/>).
    /// </summary>
    public RconConnectionStatus ConnectionStatus { get; set; } = RconConnectionStatus.Disconnected;

    /// <summary>
    /// Gets or sets a short human-readable detail for <see cref="ConnectionStatus"/> (e.g. an error
    /// message), as last reported by <see cref="ConnectionStatusChanged"/>.
    /// </summary>
    [MaxLength(200)]
    public string? ConnectionStatusDetail { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="ConnectionStatus"/> last changed.
    /// </summary>
    public DateTimeOffset? ConnectionStatusChangedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the id of the <c>RustArchon.Worker</c> instance currently responsible for this
    /// server's connection, as last reported by <see cref="ServerConnectionHeartbeat"/>. Internal
    /// ownership/liveness plumbing only - never exposed through <c>RustServerDto</c>.
    /// </summary>
    public Guid? AssignedWorkerId { get; set; }

    /// <summary>
    /// Gets or sets when the owning worker last heartbeated for this server. <c>ServerClaimSweepService</c>
    /// re-publishes a <see cref="ConnectToServer"/> claim for any enabled server whose heartbeat is
    /// null or older than its staleness threshold, which is what makes a crashed worker's servers get
    /// picked up by a survivor. Internal plumbing only - never exposed through <c>RustServerDto</c>.
    /// </summary>
    public DateTimeOffset? LastHeartbeatUtc { get; set; }

    /// <summary>
    /// Gets or sets the Steam Web API key used to look up VAC/game-ban status and hours-on-record for
    /// players on this server, encrypted at rest. Set only via
    /// <see cref="Infrastructure.Security.IApiKeyProtector.Protect"/> - never store plaintext here.
    /// <c>null</c> means this server has no Steam integration configured (those columns on
    /// <see cref="PlayerSession"/> simply stay unpopulated).
    /// </summary>
    public string? SteamApiKey { get; set; }

    /// <summary>
    /// Gets or sets which geolocation/VPN-detection provider (if any) to use for players connecting
    /// to this server. <see cref="GeolocationProviderKind.None"/> (the default) skips lookups
    /// entirely - see <see cref="Infrastructure.Geolocation.IGeolocationService"/>.
    /// </summary>
    public GeolocationProviderKind GeolocationProvider { get; set; } = GeolocationProviderKind.None;

    /// <summary>
    /// Gets or sets the API key for <see cref="GeolocationProvider"/>, encrypted at rest. Set only via
    /// <see cref="Infrastructure.Security.IApiKeyProtector.Protect"/> - never store plaintext here.
    /// </summary>
    public string? GeolocationApiKey { get; set; }

    /// <summary>
    /// Gets or sets whether the optional RustArchon companion plugin should record player positions and
    /// events on this server. On by default with an opt-out (Scott, 2026-09-19). This is the <em>desired</em>
    /// state: the Api pushes it to the plugin whenever the plugin's reported state differs (see
    /// <see cref="ServerPluginStatus"/> and <c>PluginSettingsSynchronizer</c>). Has no effect on a server
    /// without the plugin.
    /// </summary>
    public bool PluginRecordingEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the optional RustArchon companion plugin should track combat (damage) on this
    /// server. On by default; a separate switch so the highest-volume hook can be turned off from the Panel
    /// if it ever proves costly. Desired state, reconciled like <see cref="PluginRecordingEnabled"/>.
    /// </summary>
    public bool PluginCombatLogEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether an admin may update the RustArchon plugin on this server from the Panel. <b>Off by
    /// default</b> (unlike the two switches above): an update replaces code running with full server privileges, so it
    /// is opt-in per server. Even when on, nothing updates by itself; an admin has to press Update, which mints a
    /// single-use token and tells the Updater plugin to fetch and verify the new version.
    /// </summary>
    public bool PluginUpdatesEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the Panel updates the plugin and its Updater on this server <b>by itself</b> when a newer version is being
    /// served (see <c>PluginAutoUpdater</c>). Off by default, and never on while <see cref="PluginUpdatesEnabled"/> is off: it only
    /// carries out what an administrator could have done by pressing the buttons, so it changes nothing about what is trusted or checked.
    /// </summary>
    public bool PluginAutoUpdateEnabled { get; set; }

    /// <summary>
    /// Gets or sets the secret in this server's report-forwarding address, encrypted at rest (ADR-0001). <c>null</c> until an
    /// authorized user first asks for the address - and while it is <c>null</c> the server accepts no reports at all, so an
    /// existing server is closed until forwarding is deliberately turned on. Independently random per server, never derived from
    /// anything else, and reversible (not hashed) because the Panel shows the address again on request. Set only via
    /// <see cref="Infrastructure.Security.IApiKeyProtector.Protect"/>.
    /// </summary>
    public string? ReportsSecret { get; set; }

    /// <summary>
    /// Gets or sets when a check last confirmed the game server's <c>server.reportsServerEndpoint</c> is set to this server's
    /// current address. Cleared whenever the secret is rotated, since the old address no longer counts.
    /// </summary>
    public DateTimeOffset? ReportForwardingVerifiedAtUtc { get; set; }
}
