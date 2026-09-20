// Copyright ©2026 Scott Blomfield

using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Services;

/// <summary>What an ingested report turned into.</summary>
/// <param name="ReportId">The report's id.</param>
/// <param name="Merged">True when it was folded into a report that had already arrived by the other route (ADR-0003).</param>
public sealed record ReportIngestResult(Guid ReportId, bool Merged);

/// <summary>A report as the RustArchon plugin delivers it.</summary>
public sealed record PluginReportInput(
    string? ReporterSteamId,
    string? ReporterName,
    string? TargetSteamId,
    string? TargetName,
    ServerReportType Type,
    string? Subject,
    string? Message,
    string? DetailJson,
    string RawPayload);

/// <summary>
/// Turns what a game server (or its plugin) sent into a <see cref="ServerReport"/>. The one place the merge rule lives, so both
/// delivery routes apply it identically (ADR-0003).
/// </summary>
/// <remarks>
/// The caller has already decided the sender may report for this server (the token check for the native route, the plugin's
/// handshake for the other); nothing here re-decides that. What it does decide is that nothing sent is trusted: text is bounded,
/// the picture is checked by its own bytes rather than by what it claims to be, and a payload it cannot read is stored and
/// flagged instead of dropped.
/// </remarks>
public interface IReportIngestService
{
    /// <summary>Ingests what the game server posted to its <c>reportsServerEndpoint</c>: the <c>userid</c> and <c>data</c> form fields.</summary>
    Task<ReportIngestResult> IngestNativeAsync(RustServer server, string? userId, string? dataJson, CancellationToken cancellationToken = default);

    /// <summary>Ingests a report the plugin delivered.</summary>
    Task<ReportIngestResult> IngestPluginAsync(RustServer server, PluginReportInput input, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class ReportIngestService(
    IServerReportRepository reports,
    IObjectStorage storage,
    IHubContext<RconHub> hub,
    TimeProvider clock,
    ILogger<ReportIngestService> logger) : IReportIngestService
{
    /// <summary>
    /// How close together two arrivals have to be to count as the same report. Provisional (ADR-0003): there is no real traffic to
    /// tune it against yet, and per-route raw payloads are what make a wrong merge recoverable.
    /// </summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(120);

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 16 };

    /// <inheritdoc />
    public async Task<ReportIngestResult> IngestNativeAsync(
        RustServer server, string? userId, string? dataJson, CancellationToken cancellationToken = default)
    {
        var parsed = ParseNative(userId, dataJson);
        return await MergeOrCreateAsync(server, parsed, ServerReportSource.Native, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReportIngestResult> IngestPluginAsync(
        RustServer server, PluginReportInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var parsed = new ParsedReport
        {
            ReporterSteamId = NormalizeSteamId(input.ReporterSteamId),
            ReporterName = Cut(input.ReporterName, ServerReport.MaxNameLength),
            TargetSteamId = NormalizeSteamId(input.TargetSteamId),
            TargetName = Cut(input.TargetName, ServerReport.MaxNameLength),
            Type = NormalizeType(input.Type),
            Subject = Cut(input.Subject, ServerReport.MaxSubjectLength) ?? string.Empty,
            Message = Cut(input.Message, ServerReport.MaxMessageLength) ?? string.Empty,
            PluginDetailJson = Cut(input.DetailJson, ServerReport.MaxPayloadLength),
            RawPayload = Cut(input.RawPayload, ServerReport.MaxPayloadLength)
        };

        return await MergeOrCreateAsync(server, parsed, ServerReportSource.Plugin, cancellationToken);
    }

    private async Task<ReportIngestResult> MergeOrCreateAsync(
        RustServer server, ParsedReport parsed, ServerReportSource source, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var candidate = await reports.FindMergeCandidateAcrossTenantsAsync(
            server.Id, parsed.ReporterSteamId, parsed.Type, parsed.Subject, now - MergeWindow, source);

        ServerReport report;
        var merged = candidate is not null;
        string? storedImageKey = null;

        if (candidate is not null)
        {
            report = candidate;

            // Fill what the earlier arrival lacked; never overwrite what it has. Each route's own raw payload is kept apart.
            report.ReporterName ??= parsed.ReporterName;
            report.TargetSteamId ??= parsed.TargetSteamId;
            report.TargetName ??= parsed.TargetName;
            report.Position ??= parsed.Position;
            report.MinutesPlayed ??= parsed.MinutesPlayed;
            if (string.IsNullOrEmpty(report.Message))
            {
                report.Message = parsed.Message;
            }

            // Understood by either route means understood.
            report.ParseFailed = report.ParseFailed && parsed.ParseFailed;
        }
        else
        {
            report = new ServerReport
            {
                Id = Guid.NewGuid(),
                TenantId = server.TenantId,
                RustServerId = server.Id,
                ReceivedAtUtc = now,
                Type = parsed.Type,
                Status = ServerReportStatus.New,
                ReporterSteamId = parsed.ReporterSteamId,
                ReporterName = parsed.ReporterName,
                TargetSteamId = parsed.TargetSteamId,
                TargetName = parsed.TargetName,
                Subject = parsed.Subject,
                Message = parsed.Message,
                Position = parsed.Position,
                MinutesPlayed = parsed.MinutesPlayed,
                ParseFailed = parsed.ParseFailed
            };
        }

        report.Source |= source;

        if (source == ServerReportSource.Native)
        {
            report.NativePayload = parsed.RawPayload;
        }
        else
        {
            report.PluginPayload = parsed.RawPayload;
            report.PluginDetailJson = parsed.PluginDetailJson;
        }

        if (parsed.Image is not null && report.ScreenshotObjectKey is null)
        {
            storedImageKey = $"reports/{server.Id}/{report.Id}.{parsed.ImageExtension}";
            await storage.PutAsync(storedImageKey, parsed.Image, parsed.ImageContentType!, cancellationToken);
            report.ScreenshotObjectKey = storedImageKey;
            report.ScreenshotBytes = parsed.Image.Length;
        }

        try
        {
            if (merged)
            {
                await reports.UpdateAsync(report);
            }
            else
            {
                await reports.AddAsync(report);
            }
        }
        catch
        {
            // Do not leave a picture nobody can reach.
            if (storedImageKey is not null)
            {
                await TryDeleteAsync(storedImageKey);
            }

            throw;
        }

        try
        {
            // Only "something changed" goes down the hub, never the report: the group is everyone who can see the server, which is
            // wider than everyone who may read its reports. The Panel re-reads through the endpoint that checks the permission.
            await hub.Clients.Group(RconHub.GroupName(server.Id)).SendAsync("ReceiveServerReportsChanged", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Telling watchers of server {ServerId} about a new report failed; it is stored regardless.", server.Id);
        }

        return new ReportIngestResult(report.Id, merged);
    }

    private ParsedReport ParseNative(string? userId, string? dataJson)
    {
        var reporter = NormalizeSteamId(userId);

        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return new ParsedReport { ReporterSteamId = reporter, ParseFailed = true };
        }

        try
        {
            if (JsonNode.Parse(dataJson, documentOptions: ParseOptions) is not JsonObject root)
            {
                return Unreadable(reporter, dataJson);
            }

            var appInfo = root["AppInfo"] as JsonObject;

            // The picture is stored on its own; leaving it in the raw payload would put megabytes of base64 in a database row.
            byte[]? image = null;
            string? imageType = null;
            string? imageExtension = null;
            if (appInfo is not null && appInfo.Remove("Image", out var imageNode) && ReadString(imageNode) is { Length: > 0 } encoded)
            {
                DecodeImage(encoded, out image, out imageType, out imageExtension);
            }

            return new ParsedReport
            {
                ReporterSteamId = reporter ?? NormalizeSteamId(ReadString(appInfo?["UserId"])),
                ReporterName = Cut(ReadString(appInfo?["UserName"]), ServerReport.MaxNameLength),
                TargetSteamId = NormalizeSteamId(ReadString(root["TargetId"])),
                TargetName = Cut(ReadString(root["TargetName"]), ServerReport.MaxNameLength),
                Type = ReadType(root["Type"]),
                Subject = Cut(ReadString(root["Subject"]), ServerReport.MaxSubjectLength) ?? string.Empty,
                Message = Cut(ReadString(root["Message"]), ServerReport.MaxMessageLength) ?? string.Empty,
                Position = Cut(ReadString(appInfo?["LevelPos"]), ServerReport.MaxPositionLength),
                MinutesPlayed = ReadInt(appInfo?["MinutesPlayed"]),
                Image = image,
                ImageContentType = imageType,
                ImageExtension = imageExtension,
                RawPayload = Cut(root.ToJsonString(), ServerReport.MaxPayloadLength)
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            logger.LogDebug(ex, "A report's payload could not be parsed; storing it unread.");
            return Unreadable(reporter, dataJson);
        }
    }

    private static ParsedReport Unreadable(string? reporter, string raw) => new()
    {
        ReporterSteamId = reporter,
        ParseFailed = true,
        RawPayload = Cut(raw, ServerReport.MaxPayloadLength)
    };

    // The picture is identified by its own first bytes, not by anything the payload says about it: only a JPEG or a PNG is stored.
    private static void DecodeImage(string encoded, out byte[]? image, out string? contentType, out string? extension)
    {
        image = null;
        contentType = null;
        extension = null;

        var comma = encoded.IndexOf(',', StringComparison.Ordinal);
        var body = encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0 ? encoded[(comma + 1)..] : encoded;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            (image, contentType, extension) = (bytes, "image/jpeg", "jpg");
        }
        else if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            (image, contentType, extension) = (bytes, "image/png", "png");
        }
    }

    private async Task TryDeleteAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing the orphaned report picture {Key} failed.", key);
        }
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }

    private static ServerReportType ReadType(JsonNode? node)
    {
        if (ReadInt(node) is { } number)
        {
            return NormalizeType((ServerReportType)number);
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            && Enum.TryParse<ServerReportType>(text, ignoreCase: true, out var named)
            ? NormalizeType(named)
            : ServerReportType.General;
    }

    // A number Rust adds one day is still a report; it is filed as General rather than dropped.
    private static ServerReportType NormalizeType(ServerReportType type) =>
        Enum.IsDefined(type) ? type : ServerReportType.General;

    /// <summary>A SteamID64 is 17 digits; anything that is not just digits (and short enough) is not one.</summary>
    private static string? NormalizeSteamId(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 20)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }

        return trimmed;
    }

    private static string? Cut(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private sealed class ParsedReport
    {
        public string? ReporterSteamId { get; init; }
        public string? ReporterName { get; init; }
        public string? TargetSteamId { get; init; }
        public string? TargetName { get; init; }
        public ServerReportType Type { get; init; }
        public string Subject { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string? Position { get; init; }
        public int? MinutesPlayed { get; init; }
        public byte[]? Image { get; init; }
        public string? ImageContentType { get; init; }
        public string? ImageExtension { get; init; }
        public string? PluginDetailJson { get; init; }
        public string? RawPayload { get; init; }
        public bool ParseFailed { get; init; }
    }
}
