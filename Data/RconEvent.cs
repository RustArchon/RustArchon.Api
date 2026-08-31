// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A single captured WebRCON frame (console output, chat, a kill-feed line, or a command's
/// response) for one registered server.
/// </summary>
/// <remarks>
/// Derives from <see cref="Entity"/>, not <c>AuditableEntity</c>/<c>AuditableNamedEntity</c> - there
/// is no acting user for a system-captured event, no legitimate update path (this is append-only),
/// and soft-delete semantics don't apply (future retention pruning is a real hard delete by design,
/// not something a user can undo). <see cref="Type"/> is captured verbatim as a string, matching
/// <c>RconFrameCaptured.Type</c> - no enum. Classifying frames (chat vs. kill-feed vs. generic
/// console spam) is explicitly deferred to a later reader-side layer.
/// </remarks>
[Table("RconEvent")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(CapturedAtUtc),
    IsDescending = new[] { false, false, true },
    Name = "IX_RconEvent_TenantId_RustServerId_CapturedAtUtc")]
public class RconEvent : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Gets or sets the id of the <see cref="RustServer"/> this event was captured from.
    /// </summary>
    public Guid RustServerId { get; set; }

    /// <summary>
    /// Gets or sets when this frame was captured (set from <c>RconFrameCaptured.CapturedAtUtc</c>,
    /// not when it was persisted - the two are usually close, but a broker delay shouldn't be
    /// mistaken for when the event actually happened on the server).
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the WebRCON frame's correlation identifier. Nonzero when this frame is the
    /// response to a specific command; zero for unsolicited console/chat output.
    /// </summary>
    public int Identifier { get; set; }

    /// <summary>
    /// Gets or sets the raw WebRCON frame type, captured verbatim - see this class's remarks.
    /// </summary>
    /// <remarks>
    /// No explicit <c>[MaxLength]</c>/<c>[Column(TypeName = ...)]</c> on this or the two properties
    /// below - an unbounded <see cref="string"/> already maps to each provider's own "unlimited
    /// text" type by convention (<c>text</c> on PostgreSQL), matching <see cref="RustServer.RconPassword"/>'s
    /// own reasoning.
    /// </remarks>
    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Stacktrace { get; set; }
}
