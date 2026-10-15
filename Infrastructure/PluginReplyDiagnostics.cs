// Copyright ©2026 Scott Blomfield

using System;
using Microsoft.Extensions.Logging;
using RustArchon.Diagnostics;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// What the Api says when the RustArchon plugin on a game server answers one of its commands with something that is not the JSON envelope it expects
/// (change 003). Every such reply used to be called "the plugin may be too old" and the reply itself was logged nowhere, so a plugin that did exactly what
/// it was asked looked like a plugin that could not. Now the start of the reply is logged (redacted, bounded) and the person is told the truth.
/// </summary>
public static class PluginReplyDiagnostics
{
    /// <summary>The refusal code for a reply that could not be read. Not recorded as an attempt: nothing is known about whether the plugin acted.</summary>
    public const string UnreadableCode = "unreadable_reply";

    public const string UnreadableMessage = "The server's plugin answered, but the answer could not be read. The details are in the diagnostic log.";

    /// <summary>The most of a reply that is logged; the rest is cut and the entry says so.</summary>
    public const int MaxLoggedReplyCharacters = 500;

    /// <summary>
    /// Whether the reply is Rust's own answer to a command nothing has registered. That one is evidence the plugin predates the command, so it is the only
    /// unreadable reply still reported as a plugin that is too old.
    /// </summary>
    public static bool IsUnknownCommand(string? reply) =>
        reply is not null && reply.TrimStart().StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase);

    public static void LogUnreadable(ILogger logger, Guid serverId, string command, string? reply)
    {
        var shown = string.IsNullOrEmpty(reply) ? "(empty)" : reply;
        var cut = shown.Length > MaxLoggedReplyCharacters;
        logger.LogWarning(
            "The RustArchon plugin on server {ServerId} answered {Command} with a reply that could not be read{Cut}: {Reply}",
            serverId, command, cut ? " (the first " + MaxLoggedReplyCharacters + " characters shown; the rest was cut)" : string.Empty,
            LogRedactor.Redact(cut ? shown[..MaxLoggedReplyCharacters] : shown));
    }
}
