// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.Auditing;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using RustArchon.Messaging.Contracts;

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
[Table("RustServer")]
[Index(nameof(TenantId), nameof(Name), IsUnique = true, Name = "IX_RustServer_TenantId_Name")]
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
}
