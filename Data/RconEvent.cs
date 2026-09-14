// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Data;

/// <summary>
/// A single captured WebRCON frame (console output, chat, a kill-feed line, a command's response, or
/// a command that was sent) for one registered server.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="Entity"/>, not <c>AuditableEntity</c>/<c>AuditableNamedEntity</c> - there
/// is no acting user for a system-captured event, no legitimate update path (this is append-only),
/// and soft-delete semantics don't apply (future retention pruning is a real hard delete by design,
/// not something a user can undo). <see cref="Type"/> is captured verbatim as a string, matching
/// <c>RconFrameCaptured.Type</c> - no enum. Classifying frames (chat vs. kill-feed vs. generic
/// console spam) is explicitly deferred to a later reader-side layer.
/// </para>
/// <para>
/// Every captured frame is persisted regardless of <see cref="Interactive"/> - see
/// <c>RconFrameCaptured</c>'s remarks for why this table itself makes no suppress-or-not decision.
/// <see cref="Interactive"/> is filter data, not access control by itself: everywhere this entity is
/// read back out (<c>RconEventRepository.GetForServerAsync</c>'s default, <c>RconHub</c>'s group
/// split) applies the actual restriction - a <c>false</c> row must never reach an ordinary tenant
/// user, only a site admin who is both acting as this tenant and has opted into the unfiltered view.
/// </para>
/// </remarks>
[Table("RconEvent")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(CapturedAtUtc),
    IsDescending = new[] { false, false, true },
    Name = "IX_RconEvent_TenantId_RustServerId_CapturedAtUtc")]
public class RconEvent : Entity, ITenantScoped
{
    /// <summary>
    /// The <see cref="Type"/> value RustWebRconClient reports for a chat frame - the one bit of
    /// classification consumers (currently just <see cref="Repositories.RconEventRepository"/>'s
    /// <c>isChat</c> filter) rely on ahead of the full reader-side classification layer this class's
    /// remarks defer to.
    /// </summary>
    public const string ChatFrameType = "Chat";

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

    /// <summary>
    /// Gets or sets whether a human actually triggered this - see <c>RconFrameCaptured.Interactive</c>'s
    /// remarks. Everywhere this entity is read back out must filter on this; the entity itself does not.
    /// </summary>
    public bool Interactive { get; set; }

    /// <summary>
    /// Gets or sets whether this row is the command RustArchon sent, or something received back over
    /// the connection - see <see cref="RconEventDirection"/>.
    /// </summary>
    public RconEventDirection Direction { get; set; }
}
