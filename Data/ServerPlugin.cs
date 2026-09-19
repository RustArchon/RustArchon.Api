// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Data;

/// <summary>
/// One plugin currently loaded on a server, as last reported by the Worker's periodic
/// <c>o.plugins</c>/<c>c.plugins</c> poll - see <see cref="ServerPluginsCaptured"/>.
/// </summary>
/// <remarks>
/// Unlike <see cref="ServerInfoSnapshot"/> and <see cref="ConnectionLogEntry"/> this is not
/// append-only history: it is the server's <em>current</em> plugin set, and each poll makes a
/// server's rows match the reported list exactly (see <c>IServerPluginRepository.ReplaceForServerAsync</c>).
/// Derives from <see cref="Entity"/>, not an auditable variant - there is no acting user for a
/// system-captured row.
/// </remarks>
[Table("ServerPlugin")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(Name),
    Name = "IX_ServerPlugin_TenantId_RustServerId_Name")]
public class ServerPlugin : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>Which framework reported this plugin - never <see cref="ServerModFramework.None"/>, since
    /// that means "no plugins", i.e. no rows at all.</summary>
    public ServerModFramework Framework { get; set; }

    /// <summary>When the poll that last confirmed this plugin was loaded ran.</summary>
    public DateTimeOffset CapturedAtUtc { get; set; }
}
