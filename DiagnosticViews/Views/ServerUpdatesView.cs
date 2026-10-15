// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Services;

namespace RustArchon.Api.DiagnosticViews.Views;

/// <summary>
/// <c>server-updates</c>: why each outdated plugin on a server is, or is not, being updated. For every plugin with an update notice, the offer and the reason it
/// is blocked (from <see cref="IThirdPartyPluginUpdateService"/>, the same code the Plugins tab uses, so the two cannot disagree); the update attempts so far;
/// and whether and when the automatic updater will act on the server.
/// </summary>
public sealed class ServerUpdatesView(
    ApiDbContext context, IThirdPartyPluginUpdateService updates, IPlatformSettingsCache settings, ThirdPartyAutoUpdateStatus pass, TimeProvider clock) : IDiagnosticView
{
    /// <summary>How many of the most recent attempts of each kind are returned.</summary>
    public const int MaxAttempts = 20;

    public static ViewDefinition Definition { get; } = new(
        "server-updates", 1, "Why each outdated plugin on a server is or is not being updated, the attempts so far, and what the automatic updater will do.",
        [new ViewParameter("serverId", "uuid", Required: true, "The game server's id.")],
        [
            new ViewPartDefinition(
                "automaticUpdater", ViewPartKind.Object, "Whether the automatic updater will act on this server, and when it last ran and next runs.", null,
                [
                    new ViewField("platformSwitchOn", "boolean", false),
                    new ViewField("state", "string", false),
                    new ViewField("message", "string", false),
                    new ViewField("lastPassUtc", "datetime", true),
                    new ViewField("nextPassUtc", "datetime", true)
                ]),
            new ViewPartDefinition(
                "offers", ViewPartKind.List, "One row per plugin with an update notice: what can be done about it and why not.", null,
                [
                    new ViewField("pluginName", "string", false),
                    new ViewField("installedVersion", "string", true),
                    new ViewField("latestVersion", "string", true),
                    new ViewField("marketplace", "string", true),
                    new ViewField("offerState", "string", false),
                    new ViewField("detail", "string", false),
                    new ViewField("blockCode", "string", true),
                    new ViewField("installedOnServer", "boolean", false),
                    new ViewField("downloadLookup", "string", false),
                    new ViewField("fileValidation", "string", false),
                    new ViewField("canApply", "boolean", false),
                    new ViewField("fileKind", "string", false),
                    new ViewField("fileSha256", "string", false),
                    new ViewField("automaticUpdatesPausedUntilUtc", "datetime", true)
                ]),
            new ViewPartDefinition("notices", ViewPartKind.List, "The update notices the server's UpdateChecker reported.", typeof(PluginUpdateNotice), []),
            new ViewPartDefinition("exclusions", ViewPartKind.List, "Plugins excluded from updates on this server.", typeof(PluginUpdateExclusion), []),
            new ViewPartDefinition("thirdPartyAttempts", ViewPartKind.List, "Recent updates of other plugins, newest first.", typeof(ThirdPartyPluginUpdate), []),
            new ViewPartDefinition("pluginAttempts", ViewPartKind.List, "Recent updates of the RustArchon plugin and its Updater, newest first.", typeof(PluginUpdateAttempt), [])
        ]);

    public async Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken)
    {
        var id = arguments.GetGuid("serverId");
        var server = await context.RustServers.AcrossAllTenants().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ViewAnswer.NotFound("There is no game server with that id.");
        }

        var now = clock.GetUtcNow();
        var notices = await context.PluginUpdateNotices.AcrossAllTenants().AsNoTracking().Where(n => n.RustServerId == id).OrderBy(n => n.Name).ToListAsync(cancellationToken);
        var exclusions = await context.PluginUpdateExclusions.AcrossAllTenants().AsNoTracking().Where(e => e.RustServerId == id).OrderBy(e => e.NormalizedName).ToListAsync(cancellationToken);
        var thirdParty = await context.ThirdPartyPluginUpdates.AcrossAllTenants().AsNoTracking().Where(u => u.RustServerId == id)
            .OrderByDescending(u => u.StartedAtUtc).Take(MaxAttempts).ToListAsync(cancellationToken);
        var own = await context.PluginUpdateAttempts.AcrossAllTenants().AsNoTracking().Where(u => u.RustServerId == id)
            .OrderByDescending(u => u.StartedAtUtc).Take(MaxAttempts).ToListAsync(cancellationToken);
        var offers = await updates.GetOffersAsync(server, now);
        var installed = (await context.ServerPlugins.AcrossAllTenants().AsNoTracking().Where(p => p.RustServerId == id).ToListAsync(cancellationToken))
            .GroupBy(p => PluginUpdateNoticeRepository.Normalize(p.Name)).ToDictionary(g => g.Key, g => g.First());
        var noticeNames = notices.Select(n => n.NormalizedName).Distinct().ToList();
        var lookups = await context.PluginDownloadLookups.AsNoTracking().Where(l => noticeNames.Contains(l.NormalizedName)).ToListAsync(cancellationToken);

        var switchOn = await settings.GetBooleanAsync(PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled, defaultValue: true);
        var refusal = switchOn ? await updates.GetServerRefusalAsync(server, now, automatic: true) : null;
        var snapshot = pass.Current;

        return ViewAnswer.Ok(new Dictionary<string, object?>
        {
            ["automaticUpdater"] = new Dictionary<string, object?>
            {
                ["platformSwitchOn"] = switchOn,
                ["state"] = !switchOn ? "platform_switch_off" : refusal?.Code ?? "eligible",
                ["message"] = !switchOn
                    ? "Automatic third-party plugin updates are switched off for the whole platform (the ThirdPartyPluginAutoUpdatesEnabled platform setting)."
                    : refusal?.Message ?? string.Empty,
                ["lastPassUtc"] = snapshot.LastPassStartedUtc,
                ["nextPassUtc"] = snapshot.NextPassDueUtc
            },
            ["offers"] = notices.Select(n => OfferRow(n, offers.GetValueOrDefault(n.NormalizedName), installed.GetValueOrDefault(n.NormalizedName), FindLookup(lookups, n))).ToList(),
            ["notices"] = notices.Select(EntityProjector.Project).ToList(),
            ["exclusions"] = exclusions.Select(EntityProjector.Project).ToList(),
            ["thirdPartyAttempts"] = thirdParty.Select(EntityProjector.Project).ToList(),
            ["pluginAttempts"] = own.Select(EntityProjector.Project).ToList()
        });
    }

    private static PluginDownloadLookup? FindLookup(List<PluginDownloadLookup> lookups, PluginUpdateNotice notice)
    {
        var marketplace = PluginDownloadMatcher.MarketplaceKey(notice.Marketplace);
        var version = PluginDownloadMatcher.VersionKey(notice.LatestVersion);
        return lookups.FirstOrDefault(l => l.NormalizedName == notice.NormalizedName && l.MarketplaceKey == marketplace && l.Version == version);
    }

    private static Dictionary<string, object?> OfferRow(PluginUpdateNotice notice, ThirdPartyUpdateOffer? offer, ServerPlugin? installed, PluginDownloadLookup? lookup) => new()
    {
        ["pluginName"] = notice.Name,
        ["installedVersion"] = installed?.Version ?? notice.CurrentVersion,
        ["latestVersion"] = notice.LatestVersion,
        ["marketplace"] = notice.Marketplace,
        ["offerState"] = offer?.State ?? "none",
        ["detail"] = offer?.Detail is { Length: > 0 } detail ? detail : offer is null ? WhyNoOffer(notice, installed, lookup) : string.Empty,
        ["blockCode"] = offer?.BlockCode,
        ["installedOnServer"] = installed is not null,
        ["downloadLookup"] = lookup is null ? "not looked up" : lookup.Outcome.ToString(),
        ["fileValidation"] = lookup is null ? string.Empty : lookup.ValidationState.ToString(),
        ["canApply"] = offer?.CanApply ?? false,
        ["fileKind"] = offer?.Kind ?? string.Empty,
        ["fileSha256"] = offer?.FileSha256 ?? string.Empty,
        ["automaticUpdatesPausedUntilUtc"] = offer?.HeldUntilUtc
    };

    // There is no offer: say which of the things an offer needs is missing, in the order the offers are built.
    private static string WhyNoOffer(PluginUpdateNotice notice, ServerPlugin? installed, PluginDownloadLookup? lookup)
    {
        if (installed is null)
        {
            return "The plugin is not in this server's plugin list, so there is nothing to update (it may have been removed, or the notice is out of date).";
        }

        if (!ServerPluginUpdatesController.StillOutdated(installed.Version, notice.LatestVersion))
        {
            return $"The installed version ({installed.Version}) is not older than the latest reported ({notice.LatestVersion}).";
        }

        if (lookup is null)
        {
            return "No download address has been looked up for this version yet.";
        }

        if (lookup.Outcome != PluginDownloadOutcome.Found)
        {
            return $"No download address was found for this version (lookup result: {lookup.Outcome}).";
        }

        if (lookup.ValidationState is not (PluginFileValidationState.Valid or PluginFileValidationState.NeedsInstructions))
        {
            return $"The file has not been checked, or could not be checked (state: {lookup.ValidationState}{(string.IsNullOrEmpty(lookup.ValidationReason) ? string.Empty : "; " + lookup.ValidationReason)}).";
        }

        return "The Panel has no applicable offer for this update.";
    }
}
