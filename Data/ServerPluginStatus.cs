// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Data;

/// <summary>
/// What the optional RustArchon companion plugin last told the Worker about itself on one server - its
/// version, protocol, capabilities and the state of its two switches. One row per server, updated in place.
/// </summary>
/// <remarks>
/// <para>
/// Captured from <see cref="ServerPluginHandshakeCaptured"/>. Whether the plugin is <em>installed</em> is
/// not decided here - that is read from the stored <see cref="ServerPlugin"/> list, which a later poll
/// updates even after the plugin is removed. This row can therefore go stale after an uninstall; a reader
/// must only trust it while the plugin is listed, and only for the capabilities it names (fail closed).
/// </para>
/// <para>
/// The <c>Reported*</c> fields are what the plugin said, not what was asked for. The desired state lives on
/// <see cref="RustServer.PluginRecordingEnabled"/> and <see cref="RustServer.PluginCombatLogEnabled"/>.
/// Derives from <see cref="Entity"/>, not an auditable variant: nobody acts on it, the system captures it.
/// </para>
/// </remarks>
[Table("ServerPluginStatus")]
[Index(
    nameof(TenantId), nameof(RustServerId),
    IsUnique = true,
    Name = "IX_ServerPluginStatus_TenantId_RustServerId")]
public class ServerPluginStatus : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    public int ProtocolVersion { get; set; }
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>What this plugin build can actually do, as it reported - the panel enables a feature only
    /// when its capability is listed here.</summary>
    public string[] Capabilities { get; set; } = [];

    public bool ReportedRecordingEnabled { get; set; }
    public bool ReportedCombatLogEnabled { get; set; }

    /// <summary>False when the plugin could not write its settings file, so they revert on its next reload.</summary>
    public bool SettingsPersisted { get; set; }

    /// <summary>
    /// The plugin's own check of its file against the signature a Panel put on it - one of
    /// <see cref="PluginSigningStates"/>. <c>unknown</c> for a plugin build too old to report it.
    /// </summary>
    [MaxLength(16)]
    public string SigningState { get; set; } = PluginSigningStates.Unknown;

    /// <summary>The 16-hex-character fingerprint of the key that plugin trusts; empty when it has none.</summary>
    [MaxLength(16)]
    public string SigningKeyFingerprint { get; set; } = string.Empty;

    /// <summary>When the handshake that produced these values ran.</summary>
    public DateTimeOffset CapturedAtUtc { get; set; }
}
