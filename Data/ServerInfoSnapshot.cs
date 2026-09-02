// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// One periodic <c>serverinfo</c> poll snapshot for a server, capturing only the fields the Stats
/// tab graphs over time - see <c>RustArchon.Messaging.Contracts.ServerInfoSnapshotCaptured</c>'s
/// remarks for why the rest of <c>serverinfo</c>'s payload isn't persisted here at all.
/// </summary>
/// <remarks>
/// Derives from <see cref="Entity"/>, not an auditable variant - like <see cref="PlayerSession"/>,
/// there is no acting user for a system-captured snapshot. Append-only: nothing ever updates a row
/// here once written.
/// </remarks>
[Table("ServerInfoSnapshot")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(CapturedAtUtc),
    IsDescending = new[] { false, false, true },
    Name = "IX_ServerInfoSnapshot_TenantId_RustServerId_CapturedAtUtc")]
public class ServerInfoSnapshot : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public int NetworkIn { get; set; }
    public int NetworkOut { get; set; }
    public int Memory { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; }
}
