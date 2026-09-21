// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// When a version of the plugin (or the Updater) first became the one this Panel serves to be installed automatically: the moment its staged
/// roll-out began. See <c>PluginRollout</c> (the service) for how a roll-out proceeds.
/// </summary>
/// <remarks>
/// Written once, the first time the automatic updater has servers to consider and sees the version being served; never changed, so a version
/// that returns after another was served in between (a release withdrawn, then the older one served again) is not put through the ramp a
/// second time. Platform-wide, not tenant scoped, like the releases themselves.
/// </remarks>
[Table("PluginRollout")]
[Index(nameof(Kind), nameof(Version), IsUnique = true, Name = "IX_PluginRollout_Kind_Version")]
public class PluginRollout : Entity
{
    public PluginReleaseKind Kind { get; set; }

    [Required]
    [MaxLength(64)]
    public string Version { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; }
}
