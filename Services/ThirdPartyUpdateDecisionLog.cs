// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RustArchon.Api.Services;

/// <summary>
/// What the plugin-update path says about its decisions, so that "why is this server's plugin not updating?" can be answered from the log
/// (change 001, ADR-0006). Every refusal names the server, the plugin, the gate that said no (<c>Code</c>) and the reason; the same wording is used
/// whoever asked, a person or the automatic pass.
/// </summary>
/// <remarks>
/// The automatic pass runs every few minutes and would decide the same thing each time, so what <em>it</em> says is said when it changes: a refusal
/// or a waiting offer is logged the first time and again only if the answer is different. A person's click is always logged. The memory is
/// per process and bounded; losing it (a restart) only means saying a decision once more.
/// </remarks>
public static class ThirdPartyUpdateDecisionLog
{
    public const int MaxRemembered = 10_000;

    private static readonly ConcurrentDictionary<string, string> Memory = new(StringComparer.Ordinal);

    /// <summary>How many decisions are remembered, for a test of the bound.</summary>
    public static int Remembered => Memory.Count;

    public static void Refusal(ILogger logger, Guid serverId, string plugin, string trigger, string code, string reason)
    {
        if (trigger == Data.PluginUpdateTriggers.Auto && !Changed($"refusal|{serverId}|{plugin.ToLowerInvariant()}", code))
        {
            return;
        }

        var level = code is "file_changed" or "timeout" ? LogLevel.Warning : LogLevel.Information;
        logger.Log(
            level,
            "Third-party plugin update of {Plugin} on server {ServerId} was not started ({Trigger}): {Code} - {Reason}",
            plugin, serverId, trigger, code, reason);
    }

    /// <summary>An offer the automatic pass skipped because it is not ready: says why, once per change.</summary>
    public static void OfferWaiting(ILogger logger, Guid serverId, string plugin, string state, string detail)
    {
        if (!Changed($"offer|{serverId}|{plugin.ToLowerInvariant()}", state + "|" + detail))
        {
            return;
        }

        logger.LogInformation(
            "Automatic update of {Plugin} on server {ServerId} is waiting: the offer is {OfferState} - {Detail}",
            plugin, serverId, state, detail);
    }

    /// <summary>Whether an enabled server has opted in to automatic updates. A server that has not is skipped by the automatic pass, so this is the only place that says so.</summary>
    public static void OptIn(ILogger logger, Guid serverId, bool optedIn)
    {
        if (!Changed($"optin|{serverId}", optedIn ? "in" : "out") || optedIn)
        {
            return;
        }

        logger.LogInformation(
            "Server {ServerId} has not opted in to automatic updates of other plugins (the switch on its Plugins tab), so the automatic pass does not look at it; a person can still apply updates by hand.",
            serverId);
    }

    /// <summary>How many outdated plugins the automatic pass found offers for on a server. Says so once when there are none.</summary>
    public static void Offers(ILogger logger, Guid serverId, int count)
    {
        if (!Changed($"offers|{serverId}", count == 0 ? "none" : "some") || count != 0)
        {
            return;
        }

        logger.LogInformation(
            "No outdated third-party plugins are known on server {ServerId}: the RustArchon plugin there has reported no update notices, or none still apply, so there is nothing to update.",
            serverId);
    }

    /// <summary>The platform-wide switch for automatic third-party updates. Says so when it is off, and again when it comes back on.</summary>
    public static void AutoUpdatesSwitch(ILogger logger, bool enabled)
    {
        var known = Memory.TryGetValue("switch", out _);
        if (!Changed("switch", enabled ? "on" : "off"))
        {
            return;
        }

        if (!enabled)
        {
            logger.LogWarning(
                "Automatic third-party plugin updates are switched off for the whole platform (the ThirdPartyPluginAutoUpdatesEnabled setting): no server will start one until it is switched back on.");
        }
        else if (known)
        {
            logger.LogInformation("Automatic third-party plugin updates are switched back on for the platform.");
        }
    }

    private static bool Changed(string key, string value)
    {
        if (Memory.Count >= MaxRemembered)
        {
            Memory.Clear();
        }

        var changed = true;
        Memory.AddOrUpdate(
            key,
            value,
            (_, previous) =>
            {
                changed = !string.Equals(previous, value, StringComparison.Ordinal);
                return value;
            });
        return changed;
    }
}
