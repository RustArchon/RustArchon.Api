// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Data;

/// <summary>
/// An in-game (F7) report a player filed on one of a tenant's servers - the moderation inbox's row.
/// </summary>
/// <remarks>
/// <para>
/// One row per real-world report. A report can arrive by two routes (the game server's own
/// <c>reportsServerEndpoint</c> and the RustArchon plugin) carrying different data; ADR-0003 merges them into this one row rather
/// than choosing, so <see cref="Source"/> is a flags value and each route keeps its own raw payload
/// (<see cref="NativePayload"/>, <see cref="PluginPayload"/>) so a wrong merge can be audited.
/// </para>
/// <para>
/// Everything in here came from a game server that a customer (or whoever holds a leaked address) controls, so nothing is trusted:
/// text is length-bounded on the way in, the raw payloads are kept for audit, and a payload that could not be understood is
/// stored and flagged (<see cref="ParseFailed"/>) rather than dropped. The screenshot, when there is one, is not in the row - only its
/// object key is; the raw payload never contains the picture.
/// </para>
/// <para>
/// Derives from <see cref="Entity"/> rather than an auditable variant: there is no acting user for a report arriving from a
/// game server. Reviewing is the one user action, recorded in <see cref="ReviewedByUserId"/>/<see cref="ReviewedAtUtc"/>.
/// </para>
/// </remarks>
[Table("ServerReport")]
[Index(
    nameof(TenantId), nameof(RustServerId), nameof(ReceivedAtUtc),
    IsDescending = new[] { false, false, true },
    Name = "IX_ServerReport_TenantId_RustServerId_ReceivedAtUtc")]
public class ServerReport : Entity, ITenantScoped
{
    public const int MaxNameLength = 100;
    public const int MaxSubjectLength = 200;
    public const int MaxMessageLength = 5000;
    public const int MaxPositionLength = 100;
    public const int MaxPayloadLength = 256 * 1024;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RustServerId { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public ServerReportSource Source { get; set; }

    public ServerReportType Type { get; set; }

    public ServerReportStatus Status { get; set; } = ServerReportStatus.New;

    [MaxLength(20)]
    public string? ReporterSteamId { get; set; }

    [MaxLength(MaxNameLength)]
    public string? ReporterName { get; set; }

    [MaxLength(20)]
    public string? TargetSteamId { get; set; }

    [MaxLength(MaxNameLength)]
    public string? TargetName { get; set; }

    [MaxLength(MaxSubjectLength)]
    public string Subject { get; set; } = string.Empty;

    [MaxLength(MaxMessageLength)]
    public string Message { get; set; } = string.Empty;

    /// <summary>The reporter's in-game position when they filed it, as the game formatted it.</summary>
    [MaxLength(MaxPositionLength)]
    public string? Position { get; set; }

    public int? MinutesPlayed { get; set; }

    /// <summary>The object-storage key of the attached screenshot, or <c>null</c> when there was none.</summary>
    [MaxLength(200)]
    public string? ScreenshotObjectKey { get; set; }

    public long? ScreenshotBytes { get; set; }

    /// <summary>
    /// Extra detail the plugin supplied, as JSON. Untrusted, shown and never acted on. <c>null</c> when the plugin did not report
    /// this one.
    /// </summary>
    public string? PluginDetailJson { get; set; }

    /// <summary>The native route's <c>data</c> JSON with the picture stripped out, or the (bounded) raw text when it did not parse.</summary>
    public string? NativePayload { get; set; }

    /// <summary>The plugin route's payload, kept for the same reason as <see cref="NativePayload"/>.</summary>
    public string? PluginPayload { get; set; }

    /// <summary>True when a payload could not be understood. It is still stored.</summary>
    public bool ParseFailed { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedAtUtc { get; set; }

    /// <summary>
    /// The member of the tenant working this report, or <c>null</c> when unassigned. Independent of <see cref="Status"/> and of
    /// <see cref="ReviewedByUserId"/> (who last changed the status): a report can be assigned while still new, and reassigned after.
    /// </summary>
    public Guid? AssignedToUserId { get; set; }
}
