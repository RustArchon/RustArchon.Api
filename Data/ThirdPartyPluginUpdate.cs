// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>Where a third-party plugin update stands: <see cref="ThirdPartyPluginUpdate.State"/>.</summary>
public static class ThirdPartyPluginUpdateStates
{
    /// <summary>Asked, and the server's plugin said it had started; the outcome is not known yet.</summary>
    public const string Started = "started";

    /// <summary>The new version is loaded and running.</summary>
    public const string Applied = "applied";

    /// <summary>The new file was swapped in, did not come up as the version asked for, and the old one was put back.</summary>
    public const string RolledBack = "rolled-back";

    /// <summary>Started but did not complete, and nothing new is running (the download failed, the file was not what was expected, no outcome was ever reported).</summary>
    public const string Failed = "failed";

    /// <summary>The server's plugin turned the request down; see the code.</summary>
    public const string Refused = "refused";

    /// <summary>
    /// The file the server downloaded was not the one that was checked (the author replaced it, probably under the same version number). Nothing was
    /// applied. It is never applied automatically: a person looks at the newly checked file and decides.
    /// </summary>
    public const string Changed = "changed";
}

/// <summary>
/// One time the Panel asked one server to apply a newer version of another plugin - a person's click or the automatic pass - and how it turned out. The
/// audit trail of third-party plugin updates, and what the automatic pass uses so that a version that did not work out on a server is not tried again
/// there until the plugin's author publishes another. Only requests that reached the server are recorded: a refusal the Panel made itself and a server
/// that could not be reached are not attempts.
/// </summary>
[Table("ThirdPartyPluginUpdate")]
[Index(nameof(RustServerId), nameof(StartedAtUtc), Name = "IX_ThirdPartyPluginUpdate_Server_StartedAtUtc")]
[Index(nameof(RustServerId), nameof(NormalizedName), nameof(ToVersion), Name = "IX_ThirdPartyPluginUpdate_Server_Plugin_Version")]
public class ThirdPartyPluginUpdate : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    /// <summary>The plugin as the server's plugin list names it.</summary>
    [Required]
    [MaxLength(200)]
    public string PluginName { get; set; } = string.Empty;

    /// <summary>The name in the form update notices are matched on (<c>PluginUpdateNoticeRepository.Normalize</c>).</summary>
    [Required]
    [MaxLength(200)]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>The plugin's class, as found in the file that was checked - what the server's plugin looks the installed one up by.</summary>
    [Required]
    [MaxLength(100)]
    public string ClassName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string FromVersion { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ToVersion { get; set; } = string.Empty;

    /// <summary>The <see cref="PluginDownloadLookup"/> row whose file this is, so a changed file can be looked at again.</summary>
    public Guid PluginDownloadLookupId { get; set; }

    /// <summary>The SHA-256 (lower-case hex) of the file that was checked, and that the server was told to apply.</summary>
    [Required]
    [MaxLength(64)]
    public string FileSha256 { get; set; } = string.Empty;

    /// <summary>The SHA-256 of what the server actually downloaded, when that was not <see cref="FileSha256"/>; empty otherwise.</summary>
    [MaxLength(64)]
    public string ActualSha256 { get; set; } = string.Empty;

    /// <summary>What was applied: <c>cs</c> (one plugin file) or <c>zip</c> (an archive, by a person's folder rules).</summary>
    [Required]
    [MaxLength(8)]
    public string Kind { get; set; } = "cs";

    /// <summary>
    /// For a zip: the person asked for the rules to be saved for future updates. They become trusted (used without asking) only if this update comes up
    /// loaded, so a mapping that broke a plugin is never used automatically.
    /// </summary>
    public bool SaveMapping { get; set; }

    [Required]
    [MaxLength(16)]
    public string Trigger { get; set; } = PluginUpdateTriggers.Manual;

    [Required]
    [MaxLength(16)]
    public string State { get; set; } = ThirdPartyPluginUpdateStates.Started;

    /// <summary>Why it was refused or did not complete: short and code-like, since it can come from the server's plugin.</summary>
    [MaxLength(64)]
    public string Code { get; set; } = string.Empty;

    /// <summary>What the server's plugin said about it (the reason for a rollback, say), cut short and stripped of control characters.</summary>
    [MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
