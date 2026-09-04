// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Data;

/// <summary>
/// One entry on a server's Logs tab - either a <see cref="ConnectionStatusChanged"/> transition
/// (<see cref="Status"/> set) or a <see cref="WorkerDiagnosticLogged"/> event that isn't itself a
/// connection-status change (<see cref="Status"/> null) - a parse error, a poll failure, and the like.
/// A single append-only stream of both kinds, ordered by <see cref="OccurredAtUtc"/>, is what actually
/// answers "why isn't this server connecting" from the Panel alone - see <c>ConnectionStatusConsumer</c>
/// and <c>WorkerDiagnosticLoggedConsumer</c>'s remarks for why status transitions and diagnostics are
/// published as two separate message types but land in this one table.
/// </summary>
/// <remarks>
/// Distinct from <see cref="RustServer.ConnectionStatus"/> (the current value, overwritten on every
/// transition): this is append-only, so the history survives past whatever the status happens to be
/// right now. Derives from <see cref="Entity"/>, not an auditable variant - like
/// <see cref="ServerInfoSnapshot"/>, there is no acting user for a system-captured entry.
/// </remarks>
[Table("ConnectionLogEntry")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(OccurredAtUtc),
    IsDescending = new[] { false, false, true },
    Name = "IX_ConnectionLogEntry_TenantId_RustServerId_OccurredAtUtc")]
public class ConnectionLogEntry : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    public ConnectionLogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Null for a pure diagnostic entry (a parse error, a poll failure, ...) that isn't itself a
    /// connection-status transition - only set when this entry corresponds to a real
    /// <see cref="ConnectionStatusChanged"/>, which is also what the Panel renders as the colored
    /// status badge rather than a plain <see cref="Level"/> badge.
    /// </summary>
    public RconConnectionStatus? Status { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
}
