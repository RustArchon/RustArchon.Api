// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>What was updated: <see cref="PluginUpdateAttempt.Kind"/>.</summary>
public static class PluginUpdateKinds
{
    public const string Main = "main";
    public const string Updater = "updater";
}

/// <summary>Who started it: <see cref="PluginUpdateAttempt.Trigger"/>.</summary>
public static class PluginUpdateTriggers
{
    public const string Manual = "manual";
    public const string Auto = "auto";
}

/// <summary>Where an attempt stands: <see cref="PluginUpdateAttempt.State"/>.</summary>
public static class PluginUpdateAttemptStates
{
    /// <summary>Asked, and the server said it had started; the outcome is not known yet.</summary>
    public const string Started = "started";

    /// <summary>The new version is what the server now reports.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>Asked and started, but the version never changed: the new one did not come up and was put back.</summary>
    public const string Failed = "failed";

    /// <summary>The server's plugin turned the request down (a bad signature, not newer, and so on); see the code.</summary>
    public const string Refused = "refused";
}

/// <summary>
/// One time the Panel asked one server to update the RustArchon plugin or its Updater - by a person's click or by the automatic updater - and
/// how it turned out. It is the audit trail of plugin updates, and what the automatic updater uses so that a version that failed on a server is
/// not tried again there until a newer one is served. Only requests that reached the server are recorded: a refusal the Panel made itself
/// (updates off, nothing newer) and a server that could not be reached (not connected, no answer) are not attempts.
/// </summary>
[Table("PluginUpdateAttempt")]
[Index(nameof(RustServerId), nameof(StartedAtUtc), Name = "IX_PluginUpdateAttempt_Server_StartedAtUtc")]
[Index(nameof(TenantId), nameof(StartedAtUtc), Name = "IX_PluginUpdateAttempt_Tenant_StartedAtUtc")]
public class PluginUpdateAttempt : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    [Required]
    [MaxLength(16)]
    public string Kind { get; set; } = PluginUpdateKinds.Main;

    [MaxLength(50)]
    public string FromVersion { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ToVersion { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Trigger { get; set; } = PluginUpdateTriggers.Manual;

    [Required]
    [MaxLength(16)]
    public string State { get; set; } = PluginUpdateAttemptStates.Started;

    /// <summary>Why it was refused or failed; empty otherwise. Short and code-like (it can come from the server's plugin).</summary>
    [MaxLength(64)]
    public string Code { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
