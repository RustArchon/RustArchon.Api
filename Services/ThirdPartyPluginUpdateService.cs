// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Api.Services;

/// <summary>
/// What can be done about one outdated third-party plugin on one server, as a person is shown it. <see cref="State"/> is one of
/// <see cref="ThirdPartyUpdateOfferStates"/>; the wording is the Panel's, in the person's language, and only <see cref="Detail"/> (what the server's
/// plugin said) is text from elsewhere.
/// </summary>
/// <param name="Kind"><c>cs</c> for a single plugin file, <c>zip</c> for an archive applied by a person's folder rules.</param>
/// <param name="Files">For a zip: the files in it, so instructions can be given.</param>
/// <param name="SavedRules">For a zip: the instructions saved for this plugin on this server, if any.</param>
/// <param name="UncoveredPaths">For a zip with saved instructions: the files in this version of the archive that they do not cover, which is what stops them being used.</param>
/// <param name="MappingTrusted">Whether the saved instructions have produced a working update, so an automatic update may use them.</param>
public sealed record ThirdPartyUpdateOffer(
    string State, string Detail, string FileSha256, bool CanApply, DateTimeOffset? HeldUntilUtc,
    string Kind = "cs", IReadOnlyList<ZipEntryInfo>? Files = null, IReadOnlyList<ZipMappingRule>? SavedRules = null,
    IReadOnlyList<string>? UncoveredPaths = null, bool MappingTrusted = false);

/// <summary>The states of a <see cref="ThirdPartyUpdateOffer"/>. Empty (no offer at all) is the absence of one, not a state.</summary>
public static class ThirdPartyUpdateOfferStates
{
    /// <summary>A checked <c>.cs</c> file is waiting and nothing has been tried; a person can apply it, and an opted-in server will when its gates allow.</summary>
    public const string Ready = "ready";

    /// <summary>The server has been told to apply it and has not yet reported.</summary>
    public const string Applying = "applying";

    /// <summary>It came up loaded at the new version (shown until the plugin list catches up and the notice goes).</summary>
    public const string Applied = "applied";

    /// <summary>The new file did not load and the old one was put back.</summary>
    public const string RolledBack = "rolled-back";

    /// <summary>It did not complete, and nothing new is running.</summary>
    public const string Failed = "failed";

    /// <summary>The server's plugin turned the request down.</summary>
    public const string Refused = "refused";

    /// <summary>The server downloaded a file that was not the one that was checked; nothing was applied. A person decides.</summary>
    public const string Changed = "changed";

    /// <summary>The update is a zip, which is only applied once a person has said which of its files go where.</summary>
    public const string NeedsInstructions = "needs-instructions";

    /// <summary>The file was checked and is not something that can be applied; <c>Detail</c> says why.</summary>
    public const string NotApplicable = "not-applicable";

    /// <summary>A person has said this plugin's update notice does not apply here - never applied automatically or by hand until they lift it.</summary>
    public const string Excluded = "excluded";
}

/// <summary>Applies newer versions of third-party plugins on a game server, through the RustArchon plugin on it.</summary>
public interface IThirdPartyPluginUpdateService
{
    /// <summary>
    /// Checks every precondition and tells the server's RustArchon plugin to download, verify and apply the update to one plugin. Never throws for a
    /// refusal - see <see cref="PluginUpdateResultDto.Code"/>. <paramref name="expectedSha256"/>, when given, is the file a person looked at: if the
    /// file checked now is a different one, nothing is applied.
    /// </summary>
    Task<PluginUpdateResultDto> StartAsync(
        RustServer server, string pluginName, string trigger, string? expectedSha256 = null, IReadOnlyList<ZipMappingRule>? rules = null, bool saveMapping = false);

    /// <summary>Settles the updates that were started and have not reported an outcome. Returns how many were settled.</summary>
    Task<int> ReconcileAsync(RustServer server, DateTimeOffset now);

    /// <summary>What can be done about each outdated third-party plugin on the server, by normalized plugin name. Plugins with nothing to show are absent.</summary>
    Task<Dictionary<string, ThirdPartyUpdateOffer>> GetOffersAsync(RustServer server, DateTimeOffset now);

    /// <summary>
    /// Downloads the file behind this plugin's update again, right now, and records what is found - the same check a "changed" verdict already
    /// triggers on its own, offered here so a person does not have to wait for that (or for the periodic validation job) to find out whether the
    /// file has settled, or has changed again since. Returns <c>false</c> when there is nothing to recheck yet (no notice, or no download address
    /// found for it) - never a hard failure otherwise.
    /// </summary>
    Task<bool> RecheckFileAsync(RustServer server, string pluginName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Says that this plugin's update notice does not apply on this server - the case it exists for is an UpdateChecker listing for a free build of
    /// a plugin a person actually runs a different, paid build of, where "the latest version" on record was never a newer version of what is
    /// installed. Blocks both automatic and by-hand applies until lifted; <paramref name="note"/> is kept and shown back, never required.
    /// </summary>
    Task ExcludeAsync(RustServer server, string pluginName, string? note, DateTimeOffset now);

    /// <summary>Lifts an exclusion, if there is one.</summary>
    Task IncludeAsync(RustServer server, string pluginName);
}

/// <inheritdoc cref="IThirdPartyPluginUpdateService" />
/// <remarks>
/// <para>
/// <b>Fails closed, and only presses the button.</b> The server is told to apply an update only when: the plan offers the feature; the RustArchon
/// plugin there is signed by a key this Panel knows and vouches for itself; it has the capability; the plugin is installed and outdated; a file for the
/// newer version was downloaded once and checked (a single <c>.cs</c> that reads as C#, is that plugin, and is not older than the update); and nothing
/// else is in progress on that server. An automatic start also needs the server's opt-in and to be outside its days-before-wipe window; a person's
/// click does not (their decision, made looking at the page).
/// </para>
/// <para>
/// <b>The file is never kept.</b> The server is given the address, the SHA-256 and the size that were checked, downloads its own copy, and applies it
/// only if it is that file. If it is not (the author replaced it), nothing is applied, the file is checked again here, and a person decides: an update
/// that found a changed file is never applied automatically.
/// </para>
/// <para>
/// <b>No loops.</b> An update that was refused, failed, was rolled back or found a changed file is not started again <i>automatically</i> on that
/// server for that version. A person can still try it again.
/// </para>
/// </remarks>
public partial class ThirdPartyPluginUpdateService(
    ApiDbContext context,
    IServerPluginStatusRepository statuses,
    IServerPluginRepository plugins,
    IPluginScriptService script,
    IPluginDownloadLookupRepository downloads,
    IThirdPartyPluginUpdateRepository updates,
    IPluginZipMappingRepository mappings,
    IPluginUpdateExclusionRepository exclusions,
    IThirdPartyPluginUpdateGate gate,
    IPluginFileValidationJob validation,
    IRequestClient<SendRconCommand> sendCommandClient,
    IServerPollService serverPoll,
    IPluginFileRecheckThrottle recheckThrottle,
    TimeProvider clock,
    ILogger<ThirdPartyPluginUpdateService> logger) : IThirdPartyPluginUpdateService
{
    /// <summary>A server whose plugin has not been heard from for this long is not acted on: it is not there to update.</summary>
    public static readonly TimeSpan StatusFreshFor = TimeSpan.FromMinutes(15);

    /// <summary>How long an update that was started may go without an outcome before it counts as failed. The plugin's own limits add up to about three minutes.</summary>
    public static readonly TimeSpan OutcomeWithin = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A class name as the plugin accepts one (a plain ASCII identifier); anything else could not be applied, and is not sent.</summary>
    /// <remarks>Anchored with <c>\A</c> and <c>\z</c>, not <c>^</c> and <c>$</c>: <c>$</c> also matches before a trailing newline, and these go into a one-line console command.</remarks>
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,99}\z")]
    private static partial Regex ClassNamePattern();

    /// <summary>A version as the plugin accepts one: letters, digits, dots, dashes and plus signs, at most 40. Anchored like <see cref="ClassNamePattern"/>.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9.+\-]{1,40}\z")]
    private static partial Regex VersionPattern();

    private static readonly HashSet<string> TransientRefusals = ["busy"];

    public async Task<PluginUpdateResultDto> StartAsync(
        RustServer server, string pluginName, string trigger, string? expectedSha256 = null, IReadOnlyList<ZipMappingRule>? rules = null, bool saveMapping = false)
    {
        var now = clock.GetUtcNow();
        var normalized = PluginUpdateNoticeRepository.Normalize(pluginName);
        if (normalized.Length == 0)
        {
            return Refused("no_update", "That plugin has no update waiting.");
        }

        if (!server.IsEnabled)
        {
            return Refused("server_disabled", "This server is disabled.");
        }

        var gates = await gate.EvaluateAsync(server, now);
        if (!gates.PlanOffers)
        {
            return Refused("plan_does_not_offer", "This organization's plan does not include automatic plugin updates.");
        }

        var automatic = trigger == PluginUpdateTriggers.Auto;
        if (automatic && !gates.OptedIn)
        {
            return Refused("not_opted_in", "This server has not opted in to automatic updates for other plugins.");
        }

        if (automatic && gates.State == ThirdPartyUpdateGateState.HeldForWipe)
        {
            return Refused("held_for_wipe", "Automatic updates are paused ahead of the monthly wipe.");
        }

        var status = await statuses.GetForServerAcrossTenantsAsync(server.TenantId, server.Id);
        if (status is null)
        {
            return Refused("no_handshake", "The RustArchon plugin has not reported in yet.");
        }

        if (now - status.CapturedAtUtc > StatusFreshFor)
        {
            return Refused("stale_status", "The RustArchon plugin on this server has not been heard from recently.");
        }

        // The plugin that will do this must vouch for itself, under a key this Panel knows and has not revoked: nothing is applied to a game server
        // by a plugin that is not the one this Panel manages.
        var keyState = status.SigningState == PluginSigningStates.Valid ? await script.GetKeyStateAsync(status.SigningKeyFingerprint) : null;
        if (keyState is null)
        {
            return Refused("not_signed_by_this_panel", "The RustArchon plugin on this server was not signed by this Panel, so it will not be used to update other plugins.");
        }

        if (keyState == PluginKeyState.Revoked)
        {
            return Refused("key_revoked", "The key the RustArchon plugin on this server was signed with has been revoked.");
        }

        if (!status.Capabilities.Contains(RustArchonPlugin.ThirdPartyUpdateCapability, StringComparer.Ordinal))
        {
            return Refused("plugin_too_old", "The RustArchon plugin on this server is too old to update other plugins. Update it first.");
        }

        var notice = await context.PluginUpdateNotices.AcrossAllTenants().AsNoTracking()
            .FirstOrDefaultAsync(n => n.RustServerId == server.Id && n.NormalizedName == normalized);
        var installed = (await plugins.GetForServerAcrossTenantsAsync(server.TenantId, server.Id) ?? [])
            .FirstOrDefault(p => PluginUpdateNoticeRepository.Normalize(p.Name) == normalized);
        if (notice is null || installed is null || !ServerPluginUpdatesController.StillOutdated(installed.Version, notice.LatestVersion))
        {
            return Refused("no_update", "That plugin has no update waiting.");
        }

        // Not just an automatic-only preference: a person excludes a plugin because its update notice does not describe what is actually installed
        // (most often a free listing standing in for a different, paid build), so a by-hand apply is refused the same as an automatic one.
        if (await exclusions.FindAsync(server.Id, normalized) is not null)
        {
            return Refused("excluded", "You've excluded this plugin from updates on this server.");
        }

        var lookup = await FindCheckedFileAsync(notice);
        if (lookup is null)
        {
            return Refused("no_download", "No download address has been found for that update yet.");
        }

        var isZip = lookup.ValidationState == PluginFileValidationState.NeedsInstructions;
        if (!isZip && lookup.ValidationState != PluginFileValidationState.Valid)
        {
            return Refused(
                lookup.ValidationState == PluginFileValidationState.Invalid ? "not_applicable" : "not_validated",
                lookup.ValidationState == PluginFileValidationState.Invalid
                    ? "That file was checked and cannot be applied: " + lookup.ValidationReason
                    : "That file has not been checked yet.");
        }

        var target = TargetVersion(lookup, notice);
        var shown = ShownSha(lookup);
        if (!IsApplicableFile(lookup, target))
        {
            return Refused(
                "not_applicable",
                isZip
                    ? "That archive cannot be sent to a server: its plugin file, its file list or its checks are not complete."
                    : "That file cannot be sent to a server: it is not a single plugin file with a plain class name and version.");
        }

        // A zip is applied by the folder rules a person gave: the ones sent with the request, else the ones saved for this plugin on this server. An
        // automatic start uses only saved rules that have already produced a working update.
        string? zipRules = null;
        long installBytes = 0;
        IReadOnlyList<ZipMappingRule>? usedRules = null;
        if (isZip)
        {
            if (!status.Capabilities.Contains(RustArchonPlugin.ThirdPartyZipCapability, StringComparer.Ordinal))
            {
                return Refused("zip_unsupported", "The RustArchon plugin on this server cannot unpack archives, so this update is not applied here.");
            }

            var saved = await mappings.FindAsync(server.Id, normalized);
            usedRules = rules ?? (saved is not null && (!automatic || saved.Trusted) ? saved.Rules : null);
            if (usedRules is null || usedRules.Count == 0)
            {
                return Refused("needs_instructions", "That update is a zip archive, which is only applied once you say which of its files go where.");
            }

            var plan = PlanZip(lookup, usedRules);
            if (plan.Problem is not null)
            {
                // A saved mapping that no longer covers the archive is not a fault: the update waits for a person to look.
                return Refused(automatic && rules is null ? "mapping_stopped" : "mapping_invalid", plan.Problem);
            }

            zipRules = plan.EncodedRules;
            installBytes = plan.InstallBytes;
        }

        if (expectedSha256 is not null && !string.Equals(expectedSha256, shown, StringComparison.OrdinalIgnoreCase))
        {
            return Refused("file_changed", "The file for that update has changed since you looked at it. Look at it again.");
        }

        if ((await updates.GetPendingAsync(server.Id)).Count > 0)
        {
            return Refused("in_progress", "Another plugin update is still in progress on this server.");
        }

        if (automatic && await updates.HasUnsuccessfulAsync(server.Id, normalized, target))
        {
            return Refused("already_tried", "This update has already been tried on this server and did not work out.");
        }

        // IsApplicableFile has just established that the class name is present and plain.
        var className = lookup.PluginClassName!;
        var saving = isZip && saveMapping && rules is not null;
        var request = new ThirdPartyUpdateRequest(
            server.TenantId, server.Id, installed.Name, normalized, className, installed.Version, target, lookup.Id, shown, trigger, isZip ? "zip" : "cs", saving);
        var url = PluginDownloadMatcher.SafeDownloadUrl(lookup.DownloadUrl, lookup.MarketplaceKey)!;
        var command = isZip
            ? $"archon.thirdparty.zip {className} {target} {shown} {lookup.FileSizeBytes} {installBytes} {zipRules} {url}"
            : $"archon.thirdparty.update {className} {target} {shown} {lookup.FileSizeBytes} {url}";

        try
        {
            var response = await sendCommandClient.GetResponse<RconCommandResult>(
                new SendRconCommand(server.Id, command, Interactive: false), timeout: RequestTimeout.After(s: (int)CommandTimeout.TotalSeconds));

            if (!response.Message.Success)
            {
                return Refused("not_connected", "The server is not connected right now.", installed.Version, target);
            }

            var reply = ParseReply(response.Message.Message);
            if (!reply.Accepted)
            {
                if (!TransientRefusals.Contains(reply.Code) && reply.Code != "plugin_too_old")
                {
                    await updates.RecordRefusedAsync(request, reply.Code, reply.Message, now);
                }

                return Refused(reply.Code, reply.Message, installed.Version, target);
            }

            await updates.RecordStartedAsync(request, now);

            // The rules are kept once the server has taken the request, not before, and are trusted only if the update comes up (see ReconcileAsync).
            if (saving)
            {
                await mappings.SaveAsync(server.TenantId, server.Id, normalized, usedRules!, now);
            }

            logger.LogInformation(
                "Started applying {Plugin} {From} -> {To} on server {ServerId} ({Trigger}).", installed.Name, installed.Version, target, server.Id, trigger);
            return new PluginUpdateResultDto
            {
                Started = true, Code = "started", Message = "The server accepted the update and is downloading it.", FromVersion = installed.Version, ToVersion = target
            };
        }
        catch (RequestTimeoutException)
        {
            return Refused("timeout", "The server did not answer in time.", installed.Version, target);
        }
    }

    public async Task<int> ReconcileAsync(RustServer server, DateTimeOffset now)
    {
        var settled = 0;
        foreach (var pending in await updates.GetPendingAsync(server.Id))
        {
            var report = await ReadStatusAsync(server);
            if (report is not null && report.Matches(pending))
            {
                switch (report.Phase)
                {
                    case "succeeded":
                        await AppliedAsync(pending, now);
                        settled++;
                        continue;
                    case "rolled-back":
                        await updates.ResolveAsync(pending.Id, ThirdPartyPluginUpdateStates.RolledBack, "rolled_back", report.Reason, string.Empty, now);
                        LogNoLuck(pending, server, "was rolled back");
                        settled++;
                        continue;
                    case "failed":
                        await updates.ResolveAsync(pending.Id, ThirdPartyPluginUpdateStates.Failed, CodeOf(report.Reason), report.Reason, string.Empty, now);
                        LogNoLuck(pending, server, "failed");
                        settled++;
                        continue;
                    case "mismatch":
                        await updates.ResolveAsync(pending.Id, ThirdPartyPluginUpdateStates.Changed, "file_changed", report.Reason, report.ActualSha256, now);
                        LogNoLuck(pending, server, "found a changed file");
                        await LookAgainAsync(pending.PluginDownloadLookupId);
                        settled++;
                        continue;
                }

                // Still downloading or loading: wait, unless it has gone on far too long.
            }
            else if (await IsInstalledAsync(server, pending))
            {
                // The plugin's own report is about something else (it reloaded and lost its notes), but the plugin list says the new version is running.
                await AppliedAsync(pending, now);
                settled++;
                continue;
            }

            if (now - pending.StartedAtUtc > OutcomeWithin)
            {
                await updates.ResolveAsync(pending.Id, ThirdPartyPluginUpdateStates.Failed, "no_outcome", "The server never reported how it went.", string.Empty, now);
                LogNoLuck(pending, server, "never reported an outcome");
                settled++;
            }
        }

        // At least one update just settled: the installed version the Plugins tab shows (and whether the update notice for this plugin still holds)
        // comes from the plugin-list poll, which is on its own several-minute schedule - without this, someone looking right after their update
        // finished would see the truth only once that timer next comes round. Best effort: a poll that could not be sent changes nothing about the
        // outcome that was just recorded above, so it is never a reason to fail this call. See PollServerNow's remarks.
        if (settled > 0)
        {
            try
            {
                await serverPoll.PollNowAsync(server.Id, [PollServerNowKinds.Plugins]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not poll server {ServerId} right after a third-party plugin update settled.", server.Id);
            }
        }

        return settled;
    }

    // The update came up loaded. If it was a zip applied with rules a person asked to have saved, the rules have now proved themselves and are used
    // without asking from here on.
    private async Task AppliedAsync(ThirdPartyPluginUpdate pending, DateTimeOffset now)
    {
        await updates.ResolveAsync(pending.Id, ThirdPartyPluginUpdateStates.Applied, string.Empty, string.Empty, string.Empty, now);
        if (pending.Kind == "zip" && pending.SaveMapping)
        {
            await mappings.MarkTrustedAsync(pending.RustServerId, pending.NormalizedName, now);
        }
    }

    public async Task<Dictionary<string, ThirdPartyUpdateOffer>> GetOffersAsync(RustServer server, DateTimeOffset now)
    {
        var offers = new Dictionary<string, ThirdPartyUpdateOffer>();
        var gates = await gate.EvaluateAsync(server, now);
        if (!gates.PlanOffers)
        {
            return offers;
        }

        var notices = await context.PluginUpdateNotices.AcrossAllTenants().AsNoTracking().Where(n => n.RustServerId == server.Id).ToListAsync();
        if (notices.Count == 0)
        {
            return offers;
        }

        var installed = (await plugins.GetForServerAcrossTenantsAsync(server.TenantId, server.Id) ?? [])
            .GroupBy(p => PluginUpdateNoticeRepository.Normalize(p.Name)).ToDictionary(g => g.Key, g => g.First());
        var status = await statuses.GetForServerAcrossTenantsAsync(server.TenantId, server.Id);
        var capable = status is not null && now - status.CapturedAtUtc <= StatusFreshFor
            && status.Capabilities.Contains(RustArchonPlugin.ThirdPartyUpdateCapability, StringComparer.Ordinal);
        var zipCapable = capable && status!.Capabilities.Contains(RustArchonPlugin.ThirdPartyZipCapability, StringComparer.Ordinal);
        var latest = (await updates.GetLatestPerPluginAsync(server.Id)).ToDictionary(u => u.NormalizedName);
        var busy = (await updates.GetPendingAsync(server.Id)).Count > 0;
        var excluded = (await exclusions.ListForServerAsync(server.Id)).ToDictionary(e => e.NormalizedName);

        foreach (var notice in notices)
        {
            if (!installed.TryGetValue(notice.NormalizedName, out var plugin) || !ServerPluginUpdatesController.StillOutdated(plugin.Version, notice.LatestVersion))
            {
                continue;
            }

            // Shown ahead of the file check below: a person who excluded a plugin should see that it is excluded even before anything has been
            // downloaded and checked for it, not just once there is a file to withhold.
            if (excluded.TryGetValue(notice.NormalizedName, out var exclusion))
            {
                offers[notice.NormalizedName] = new ThirdPartyUpdateOffer(ThirdPartyUpdateOfferStates.Excluded, ExcludedDetail(exclusion), string.Empty, false, null);
                continue;
            }

            var lookup = await FindCheckedFileAsync(notice);
            if (lookup is null)
            {
                continue;
            }

            var target = TargetVersion(lookup, notice);
            var shown = ShownSha(lookup);
            latest.TryGetValue(notice.NormalizedName, out var last);
            if (last is not null && last.ToVersion != target)
            {
                last = null;      // about an earlier version of this plugin
            }

            var saved = lookup.ValidationState == PluginFileValidationState.NeedsInstructions ? await mappings.FindAsync(server.Id, notice.NormalizedName) : null;
            var offer = Describe(lookup, target, shown, last, capable, zipCapable, busy, gates, saved);
            if (offer is not null)
            {
                offers[notice.NormalizedName] = offer;
            }
        }

        return offers;
    }

    private static ThirdPartyUpdateOffer? Describe(
        PluginDownloadLookup lookup, string target, string shown, ThirdPartyPluginUpdate? last, bool capable, bool zipCapable, bool busy,
        ThirdPartyUpdateGateResult gates, SavedZipMapping? saved)
    {
        var held = gates.State == ThirdPartyUpdateGateState.HeldForWipe ? gates.HeldUntilUtc : null;
        switch (lookup.ValidationState)
        {
            case PluginFileValidationState.Invalid:
                return new ThirdPartyUpdateOffer(ThirdPartyUpdateOfferStates.NotApplicable, lookup.ValidationReason, string.Empty, false, held);
            case PluginFileValidationState.Valid or PluginFileValidationState.NeedsInstructions:
                break;
            default:
                return null;      // not checked yet, or the check could not be made: nothing to say, and nothing to do
        }

        if (!IsApplicableFile(lookup, target))
        {
            return new ThirdPartyUpdateOffer(ThirdPartyUpdateOfferStates.NotApplicable, string.Empty, string.Empty, false, held);
        }

        var isZip = lookup.ValidationState == PluginFileValidationState.NeedsInstructions;
        var canTry = capable && (!isZip || zipCapable) && !busy;

        // Fail-closed states are silent by design (nothing can be done until the reason clears), but a silent button that simply is not there is not
        // diagnosable - a person looking at the row has to be able to tell WHY, not just that it is not offered right now. Empty when canTry is true:
        // nothing needs explaining.
        var blocked = canTry ? null : BlockedReason(capable, isZip, zipCapable, busy);
        string WithBlocked(string message) => blocked is null ? message : Append(message, blocked);

        // For a zip: what a person needs to give instructions (its files, and the instructions saved before), and whether the saved ones can be used
        // without asking: they have to have produced a working update before, and still cover every file in this version of the archive.
        IReadOnlyList<ZipEntryInfo>? files = null;
        List<string>? uncovered = null;
        var trustedAndCovered = false;
        if (isZip)
        {
            files = ZipListing.Parse(lookup.ZipEntries);
            if (saved is not null)
            {
                var resolved = ZipMapping.Resolve(files, saved.Rules);
                uncovered = resolved.Entries.Where(e => e.Action == ZipEntryAction.Unassigned).Select(e => e.Path).Take(20).ToList();
                trustedAndCovered = saved.Trusted && PlanZip(lookup, saved.Rules).Problem is null;
            }
        }

        ThirdPartyUpdateOffer Offer(string state, string detail, bool canApply) => new(
            state, detail, shown, canApply, held, isZip ? "zip" : "cs", files, saved?.Rules, uncovered is { Count: > 0 } ? uncovered : null, saved?.Trusted ?? false);

        if (last is null)
        {
            return isZip && !trustedAndCovered
                ? Offer(ThirdPartyUpdateOfferStates.NeedsInstructions, blocked ?? string.Empty, canTry)
                : Offer(ThirdPartyUpdateOfferStates.Ready, blocked ?? string.Empty, canTry);
        }

        return last.State switch
        {
            ThirdPartyPluginUpdateStates.Started => Offer(ThirdPartyUpdateOfferStates.Applying, string.Empty, false),
            ThirdPartyPluginUpdateStates.Applied => Offer(ThirdPartyUpdateOfferStates.Applied, string.Empty, false),
            ThirdPartyPluginUpdateStates.RolledBack => Offer(ThirdPartyUpdateOfferStates.RolledBack, WithBlocked(last.Message), canTry),
            ThirdPartyPluginUpdateStates.Failed => Offer(ThirdPartyUpdateOfferStates.Failed, WithBlocked(last.Message), canTry),
            ThirdPartyPluginUpdateStates.Refused => Offer(ThirdPartyUpdateOfferStates.Refused, WithBlocked(last.Message), canTry),

            // The server downloaded a file that did not match the one that was checked here. That is all the evidence proves - not that the author
            // changed it (a CDN edge, a header/encoding difference between how the Api and the plugin each fetch it, or plain timing between the two
            // downloads can produce the same mismatch with nobody having changed anything). Applying is offered the same as any other retryable state
            // - the person decides, not a same-hash rule that used to make this a dead end with no way to apply once checked. See ChangedOffer.
            ThirdPartyPluginUpdateStates.Changed => ChangedOffer(Offer, canTry, blocked, shown, last),
            _ => null
        };
    }

    /// <summary>Explains, in one short sentence, why a person cannot press Apply on an offer that would otherwise let them - the one thing a missing
    /// button on its own can never say. <c>null</c> means nothing is blocking it (the caller only calls this when something is).</summary>
    private static string? BlockedReason(bool capable, bool isZip, bool zipCapable, bool busy)
    {
        if (busy)
        {
            return "Another update is in progress on this server; this one waits for it to finish.";
        }

        if (!capable)
        {
            return "The RustArchon plugin on this server has not reported recently, or does not report being able to do this.";
        }

        if (isZip && !zipCapable)
        {
            return "The RustArchon plugin on this server cannot unpack archives.";
        }

        return null;   // canTry was false for a reason this method does not (yet) know how to name; better silent than wrong.
    }

    /// <summary>
    /// A changed-file offer's own reasoning, on top of what the server itself said (<paramref name="last"/>'s message): whether the file on record
    /// here still hashes the same as what was already sent (worth saying, never worth blocking on - see this state's remarks above), and whatever
    /// <paramref name="blocked"/> already says (busy, stale status, no zip support), which does still withhold Apply, the same as every other
    /// retryable state.
    /// </summary>
    private static ThirdPartyUpdateOffer ChangedOffer(Func<string, string, bool, ThirdPartyUpdateOffer> offer, bool canTry, string? blocked, string shown, ThirdPartyPluginUpdate last)
    {
        var sameFileAsBefore = string.Equals(shown, last.FileSha256, StringComparison.OrdinalIgnoreCase);
        var detail = last.Message;
        if (canTry && sameFileAsBefore)
        {
            detail = Append(detail, "The file on record here still hashes the same as what was already sent; applying again asks the server to download it once more.");
        }

        if (blocked is not null)
        {
            detail = Append(detail, blocked);
        }

        return offer(ThirdPartyUpdateOfferStates.Changed, detail, canTry);
    }

    private static string Append(string text, string sentence) => text.Length > 0 ? $"{text} {sentence}" : sentence;

    private static string ExcludedDetail(PluginUpdateExclusion exclusion) =>
        exclusion.Note.Length > 0
            ? $"You've excluded this plugin from updates on this server: {exclusion.Note}"
            : "You've excluded this plugin from updates on this server.";

    public async Task ExcludeAsync(RustServer server, string pluginName, string? note, DateTimeOffset now)
    {
        var normalized = PluginUpdateNoticeRepository.Normalize(pluginName);
        if (normalized.Length == 0)
        {
            return;
        }

        await exclusions.ExcludeAsync(server.TenantId, server.Id, normalized, note, now);
    }

    public async Task IncludeAsync(RustServer server, string pluginName)
    {
        var normalized = PluginUpdateNoticeRepository.Normalize(pluginName);
        if (normalized.Length == 0)
        {
            return;
        }

        await exclusions.IncludeAsync(server.Id, normalized);
    }

    /// <summary>The found row for this notice's plugin at its newest version, if it has a usable address; otherwise <c>null</c>.</summary>
    private async Task<PluginDownloadLookup?> FindCheckedFileAsync(PluginUpdateNotice notice)
    {
        var marketplaceKey = PluginDownloadMatcher.MarketplaceKey(notice.Marketplace);
        var row = await downloads.FindAsync(marketplaceKey, notice.NormalizedName, PluginDownloadMatcher.VersionKey(notice.LatestVersion));
        return row is { Outcome: PluginDownloadOutcome.Found } && PluginDownloadMatcher.SafeDownloadUrl(row.DownloadUrl, marketplaceKey) is not null ? row : null;
    }

    public async Task<bool> RecheckFileAsync(RustServer server, string pluginName, CancellationToken cancellationToken = default)
    {
        var normalized = PluginUpdateNoticeRepository.Normalize(pluginName);
        if (normalized.Length == 0)
        {
            return false;
        }

        var notice = await context.PluginUpdateNotices.AcrossAllTenants().AsNoTracking()
            .FirstOrDefaultAsync(n => n.RustServerId == server.Id && n.NormalizedName == normalized, cancellationToken);
        if (notice is null)
        {
            return false;
        }

        var lookup = await FindCheckedFileAsync(notice);
        if (lookup is null)
        {
            return false;
        }

        if (!recheckThrottle.TryAcquire(lookup.Id))
        {
            return true;      // Not a failure - just too soon to ask again; whatever the last check found still stands and is what GetOffersAsync shows.
        }

        // Ignores the job's own backoff (ValidationNextAttemptUtc) on purpose: that paces an automatic job hitting a marketplace on its own schedule,
        // not a person's own deliberate, rare, one-at-a-time click - see IThirdPartyPluginUpdateService.RecheckFileAsync's remarks.
        await validation.RecheckAsync(lookup.Id, cancellationToken);
        return true;
    }

    /// <summary>The version the server is asked for: the one the file says it is (which is what the plugin will report once it loads), else the notice's.</summary>
    private static string TargetVersion(PluginDownloadLookup lookup, PluginUpdateNotice notice) =>
        string.IsNullOrWhiteSpace(lookup.PluginInfoVersion) ? notice.LatestVersion : lookup.PluginInfoVersion;

    private static string ShownSha(PluginDownloadLookup lookup) => (lookup.FileSha256 ?? string.Empty).ToLowerInvariant();

    /// <summary>Whether what was recorded about the file is enough to send: a single <c>.cs</c> with a plain class name, version, hash and size.</summary>
    private static bool IsApplicableFile(PluginDownloadLookup lookup, string target)
    {
        var isZip = lookup.ValidationState == PluginFileValidationState.NeedsInstructions;
        var common = !string.IsNullOrEmpty(lookup.PluginClassName) && ClassNamePattern().IsMatch(lookup.PluginClassName)
            && VersionPattern().IsMatch(target)
            && ShownSha(lookup).Length == 64 && ShownSha(lookup).All(char.IsAsciiHexDigit)
            && lookup.FileSizeBytes is > 0 and <= ZipMapping.MaxArchiveBytes;      // a game server's plugin refuses a larger one whatever it is told
        if (!isZip)
        {
            return lookup.FileKind == "cs" && common;
        }

        // An archive whose file list was cut short cannot be checked to be covered by anyone's rules, so it is not applied.
        var files = ZipListing.Parse(lookup.ZipEntries).Count;
        return lookup.FileKind == "zip" && common && lookup.ZipSourceFindings is not null && files > 0 && files < PluginDownloadLookup.MaxZipEntries;
    }

    /// <summary>What the rules make of an archive, ready to send: the rules as one command argument and the most that will be unpacked - or what is wrong with them.</summary>
    private sealed record ZipPlan(string? EncodedRules, long InstallBytes, string? Problem);

    /// <summary>
    /// Checks a person's (or a saved) rules against the archive as it was checked: every file installed or skipped, nothing unsafe or ambiguous, and
    /// every plugin source file that would be installed one that was found to read as C#, including the plugin itself. Nothing is fetched: what each source
    /// file is was worked out once, with the archive's hash.
    /// </summary>
    private static ZipPlan PlanZip(PluginDownloadLookup lookup, IReadOnlyList<ZipMappingRule> rules)
    {
        var resolved = ZipMapping.Resolve(ZipListing.Parse(lookup.ZipEntries), rules);
        if (!resolved.IsValid)
        {
            var shown = resolved.Problems.Take(3).Select(p => p.Path.Length > 0 ? $"{p.Path}: {p.Message}" : p.Message);
            var more = resolved.Problems.Count > 3 ? $" (and {resolved.Problems.Count - 3} more)" : string.Empty;
            return new ZipPlan(null, 0, string.Join("; ", shown) + more);
        }

        var findings = ZipListing.ParseFindings(lookup.ZipSourceFindings).GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First());
        var sources = resolved.Installed.Where(e => e.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var source in sources)
        {
            if (!findings.TryGetValue(source.Path, out var finding) || finding.Problem is not null)
            {
                return new ZipPlan(null, 0, $"{source.Path}: this plugin file cannot be installed ({finding?.Problem ?? "it was not checked"})");
            }
        }

        if (!sources.Any(s => string.Equals(findings[s.Path].Class, lookup.PluginClassName, StringComparison.OrdinalIgnoreCase)))
        {
            return new ZipPlan(null, 0, "these instructions do not install the plugin's own file, so there would be nothing to update");
        }

        if (resolved.InstallBytes <= 0)
        {
            return new ZipPlan(null, 0, "these instructions install no files");
        }

        // The sizes are the archive's own claims, checked as the files are unpacked; a total past what a server will unpack is refused outright.
        if (resolved.InstallBytes > ZipMapping.MaxInstallBytes)
        {
            return new ZipPlan(null, 0, $"these instructions would install more than {ZipMapping.MaxInstallBytes / (1024 * 1024)} MB");
        }

        var encoded = ZipMapping.Encode(rules);
        return encoded is null ? new ZipPlan(null, 0, "these instructions are too long to send to a server") : new ZipPlan(encoded, resolved.InstallBytes, null);
    }

    private async Task<StatusReport?> ReadStatusAsync(RustServer server)
    {
        try
        {
            var response = await sendCommandClient.GetResponse<RconCommandResult>(
                new SendRconCommand(server.Id, "archon.thirdparty.status", Interactive: false), timeout: RequestTimeout.After(s: (int)CommandTimeout.TotalSeconds));
            return response.Message.Success ? StatusReport.Parse(response.Message.Message) : null;
        }
        catch (RequestTimeoutException)
        {
            return null;
        }
    }

    private async Task<bool> IsInstalledAsync(RustServer server, ThirdPartyPluginUpdate pending)
    {
        var installed = (await plugins.GetForServerAcrossTenantsAsync(server.TenantId, server.Id) ?? [])
            .FirstOrDefault(p => PluginUpdateNoticeRepository.Normalize(p.Name) == pending.NormalizedName);
        return installed is not null && PluginVersions.IsAtLeast(installed.Version.TrimStart('v', 'V'), pending.ToVersion.TrimStart('v', 'V'));
    }

    // The file was not the one that was checked: look at what is being served now, so a person is shown that and not the old one.
    private async Task LookAgainAsync(Guid lookupId)
    {
        try
        {
            await validation.RecheckAsync(lookupId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not check the changed file for lookup {LookupId} again.", lookupId);
        }
    }

    private void LogNoLuck(ThirdPartyPluginUpdate pending, RustServer server, string what) =>
        logger.LogWarning(
            "The update of {Plugin} to {To} on server {ServerId} {What}; it will not be tried again automatically.", pending.PluginName, pending.ToVersion, server.Id, what);

    // The plugin's reason is "<code>: <detail>"; the code is what is kept as one.
    private static string CodeOf(string reason)
    {
        var colon = reason.IndexOf(':');
        var code = colon > 0 ? reason[..colon] : string.Empty;
        return Sanitize(code) ?? "failed";
    }

    // The server's plugin answers with an envelope: {"v":1,"ok":true,...} or {"v":1,"ok":false,"err":"...","message":"..."}. Anything that is not
    // clearly ok is a refusal (fail closed), whatever the text.
    private static (bool Accepted, string Code, string Message) ParseReply(string? reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return (true, "started", string.Empty);
            }

            var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
            var message = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;
            return (false, Sanitize(code) ?? "plugin_refused", message ?? "The server's plugin refused the request.");
        }
        catch (JsonException)
        {
            // Not an envelope at all: most likely "Unknown command", i.e. the RustArchon plugin there predates this.
            return (false, "plugin_too_old", "The server did not recognize the command; its RustArchon plugin may be too old.");
        }
    }

    // The code comes from a plugin on someone else's server and is shown to a user: short and simple, or dropped.
    private static string? Sanitize(string? code) =>
        code is { Length: > 0 and <= 40 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-') ? code : null;

    private static PluginUpdateResultDto Refused(string code, string message, string? from = null, string? to = null) =>
        new() { Started = false, Code = code, Message = message, FromVersion = from, ToVersion = to };

    /// <summary>What <c>archon.thirdparty.status</c> said.</summary>
    private sealed record StatusReport(string Phase, string ClassName, string TargetVersion, string Sha256, string ActualSha256, string Reason)
    {
        /// <summary>Whether this is about the update that was started (the same plugin, version and file), and not something the plugin has done since.</summary>
        public bool Matches(ThirdPartyPluginUpdate pending) =>
            string.Equals(ClassName, pending.ClassName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TargetVersion, pending.ToVersion, StringComparison.Ordinal)
            && string.Equals(Sha256, pending.FileSha256, StringComparison.OrdinalIgnoreCase);

        public static StatusReport? Parse(string? reply)
        {
            try
            {
                using var document = JsonDocument.Parse(reply ?? string.Empty);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                    || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                string Text(string name) => data.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
                return new StatusReport(Text("phase"), Text("class"), Text("targetVersion"), Text("sha256"), Text("actualSha256"), Text("reason"));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
