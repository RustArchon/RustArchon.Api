// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Diagnostics;

namespace RustArchon.Api.DiagnosticViews.Views;

/// <summary>
/// <c>server-rcon-recent</c>: the latest console frames for a game server, oldest first, including the frames nobody typed (the commands the Api and the
/// plugin send themselves, and the replies to them). Frames are free text, so each message and stack trace goes through the log redactor before it is
/// returned; chat, names, Steam IDs and IP addresses are left as they are (the owner's decision: not secret, and the Console tab already shows them).
/// </summary>
public sealed class ServerRconRecentView(ApiDbContext context) : IDiagnosticView
{
    public const int DefaultFrames = 100;

    /// <summary>The most frames one call returns; a larger <c>count</c> is held to this.</summary>
    public const int MaxFrames = 200;

    /// <summary>The longest message or stack trace returned; a longer one is cut and its frame says so.</summary>
    public const int MaxMessageCharacters = 2000;

    public static ViewDefinition Definition { get; } = new(
        "server-rcon-recent", 1, $"The latest console frames for a game server, oldest first, with secrets redacted. count is held to {MaxFrames}.",
        [
            new ViewParameter("serverId", "uuid", Required: true, "The game server's id."),
            new ViewParameter("count", "integer", Required: false, $"How many of the latest frames (1 to {MaxFrames}).", DefaultFrames.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ],
        [
            new ViewPartDefinition(
                "frames", ViewPartKind.List, "Console frames, including those sent by the Api and the plugin themselves.", typeof(RconEvent),
                [new ViewField("truncated", "boolean", false)])
        ]);

    public async Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken)
    {
        var id = arguments.GetGuid("serverId");
        if (!await context.RustServers.AcrossAllTenants().AnyAsync(s => s.Id == id, cancellationToken))
        {
            return ViewAnswer.NotFound("There is no game server with that id.");
        }

        var count = arguments.Has("count") ? System.Math.Clamp(arguments.GetInt("count"), 1, MaxFrames) : DefaultFrames;
        var latest = await context.RconEvents.AcrossAllTenants().AsNoTracking().Where(e => e.RustServerId == id)
            .OrderByDescending(e => e.CapturedAtUtc).ThenByDescending(e => e.Id).Take(count).ToListAsync(cancellationToken);
        if (latest.Count == 0)
        {
            return ViewAnswer.Ok(
                new Dictionary<string, object?> { ["frames"] = null },
                [new UnavailablePart("frames", "No console frames have been captured for this server yet.")]);
        }

        latest.Reverse();
        return ViewAnswer.Ok(new Dictionary<string, object?> { ["frames"] = latest.Select(Row).ToList() });
    }

    private static Dictionary<string, object?> Row(RconEvent frame)
    {
        var row = EntityProjector.Project(frame);
        var truncated = false;
        row["message"] = Safe(row["message"] as string, ref truncated);
        row["stacktrace"] = Safe(row["stacktrace"] as string, ref truncated);
        row["truncated"] = truncated;
        return row;
    }

    // Cut first, then redact: the redactor sees the same text that would be sent, and a secret split by the cut cannot be half-shown.
    private static string? Safe(string? text, ref bool truncated)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Length > MaxMessageCharacters)
        {
            truncated = true;
            text = text[..MaxMessageCharacters];
        }

        return LogRedactor.Redact(text);
    }
}
