// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Infrastructure;

/// <summary>A refused key operation. <see cref="Code"/> is stable and safe to show; the message is for a log.</summary>
public sealed class PluginKeyOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One key as the admin page shows it.</summary>
/// <param name="ServersReporting">
/// How many servers' last handshake named this key, and <paramref name="LastReportedUtc"/> the latest of those. Only
/// information for the admin's decision: a server that is offline or behind reports nothing, so a low number never
/// means it is safe to drop a key (nothing here ever does).
/// </param>
/// <param name="ActiveSinceUtc">For the active key only: when it became the active one (see <see cref="IPluginKeyService.GetReminderAsync"/>).</param>
public sealed record PluginKeyInfo(
    string Fingerprint,
    PluginKeyState State,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedReason,
    int ServersReporting,
    DateTimeOffset? LastReportedUtc,
    DateTimeOffset? ActiveSinceUtc = null);

/// <summary>Whether the active signing key has gone long enough without being rotated that an administrator is reminded.</summary>
/// <param name="Fingerprint">The active key; null when there is none yet.</param>
/// <param name="ActiveSinceUtc">When it became the active key; null when that cannot be told.</param>
/// <param name="AgeDays">Whole days since <paramref name="ActiveSinceUtc"/>.</param>
/// <param name="ReminderDays">The Platform Setting: how many days before the reminder; zero means the reminder is off.</param>
/// <param name="Due">True when there is an active key, its age is known, the reminder is on, and the age has reached it.</param>
public sealed record PluginKeyReminder(string? Fingerprint, DateTimeOffset? ActiveSinceUtc, int? AgeDays, int ReminderDays, bool Due);

/// <summary>An export: the bundle's text and a suggested file name. Never logged.</summary>
public sealed record PluginKeyExport(string FileName, string Json, IReadOnlyList<string> Fingerprints);

/// <summary>What importing does (or, for a dry run, would do) to one key.</summary>
/// <param name="BundleState">Where the file says the key stands; null for a key here that the file does not mention.</param>
/// <param name="Action">
/// <c>added</c> (new here, kept in the history), <c>already_present</c>, <c>already_active</c>, <c>revoked</c> (it was
/// retired here and the file has it revoked), <c>activated</c> (it becomes the active key), or <c>previous_active_retired</c>
/// (it was the active key here and is kept in the history).
/// </param>
public sealed record PluginKeyImportItem(string Fingerprint, PluginKeyState? BundleState, string Action);

/// <summary>The outcome of an import, or the plan for one.</summary>
public sealed record PluginKeyImportResult(bool DryRun, IReadOnlyList<PluginKeyImportItem> Items, string? ActiveBefore, string? ActiveAfter)
{
    public bool ChangesActiveKey => ActiveAfter != ActiveBefore;
    public bool ChangesAnything => Items.Any(i => i.Action is "added" or "revoked" or "activated" or "previous_active_retired");
}

/// <summary>Rotating and revoking the plugin signing key, and listing them all. Site Admin actions, all audit-logged.</summary>
public interface IPluginKeyService
{
    /// <summary>
    /// Every key this Panel holds, active and history, sealed into a passphrase-protected bundle (see <see cref="PluginKeyBundle"/>)
    /// that can be backed up or imported into another Panel. Audit-logged. The bundle holds private keys: whoever has it and the
    /// passphrase can sign code every server trusting these keys will run.
    /// </summary>
    /// <exception cref="PluginKeyOperationException"><c>passphrase_weak</c>, <c>key_unreadable</c> (a stored key cannot be decrypted, so nothing is exported).</exception>
    Task<PluginKeyExport> ExportAsync(string passphrase, string actor);

    /// <summary>
    /// Merges a bundle's keys into this Panel's, by fingerprint. Keys new here are kept in the history (they can sign a bridge but
    /// not new files); a key the file has revoked is revoked here too, and a key revoked here is never brought back; the active key
    /// changes only when <paramref name="activateBundleKey"/> asks for it, and then through the same path as a rotation (the old
    /// active key is kept). With <paramref name="dryRun"/> nothing is written and the plan is returned. All or nothing.
    /// </summary>
    /// <exception cref="PluginKeyOperationException">
    /// <c>bundle_invalid</c>, <c>bundle_unreadable</c>, <c>bundle_keys_invalid</c>, <c>no_active_in_bundle</c>,
    /// <c>cannot_activate_revoked</c>, <c>revoked_in_bundle_active_here</c>, <c>concurrent_change</c>, <c>key_unreadable</c>.
    /// </exception>
    Task<PluginKeyImportResult> ImportAsync(string bundleJson, string passphrase, bool activateBundleKey, bool dryRun, string actor, string? note);

    /// <summary>The active key first, then every retired or revoked key, most recently retired first.</summary>
    Task<IReadOnlyList<PluginKeyInfo>> ListAsync();

    /// <summary>
    /// Whether the active key is old enough that a site administrator should be reminded to consider rotating it (the Platform Setting
    /// <see cref="PlatformSettingsRegistry.PluginKeyRotationReminderDays"/>, a year by default). The key's age counts from when it became the
    /// active one: the latest rotation or import that replaced the one before it, else the moment it was first made (the audit log's
    /// <see cref="PluginAdminEventKind.KeyGenerated"/> line), else - for a key made before that line existed - when its setting was first created,
    /// which is close. Only ever a reminder: nothing rotates by itself, because rotating decides what every installed plugin will trust next.
    /// </summary>
    Task<PluginKeyReminder> GetReminderAsync();

    /// <summary>
    /// Makes a new key the active one and retires the current one (kept, so servers still on it can be bridged).
    /// If <paramref name="revokeCurrent"/> the old key is revoked instead - for a suspected compromise - so there is
    /// still always exactly one active key. Returns the new active key.
    /// </summary>
    /// <exception cref="PluginKeyOperationException"><c>concurrent_change</c> when another rotation got there first.</exception>
    Task<PluginKeyInfo> RotateAsync(string actor, string? note, bool revokeCurrent = false, string? revokeReason = null);

    /// <summary>
    /// Revokes a retired key: it never signs again, and a server still on it can only be fixed by downloading the
    /// plugin by hand.
    /// </summary>
    /// <exception cref="PluginKeyOperationException">
    /// <c>not_found</c>, <c>is_active</c> (rotate first), <c>already_revoked</c>, or <c>reason_required</c>.
    /// </exception>
    Task<PluginKeyInfo> RevokeAsync(string fingerprint, string reason, string actor);
}

/// <inheritdoc cref="IPluginKeyService" />
/// <remarks>
/// <para>
/// The active key stays in the <c>PluginSigningKey</c> platform setting; rotation moves its stored (encrypted) value
/// into <see cref="PluginKeyHistory"/> and writes the new key over it, in one transaction. The write to the setting is
/// a compare-and-swap on the value read at the start, so two admins rotating at once cannot both succeed - the loser
/// gets <c>concurrent_change</c> and nothing changes. Nothing is ever deleted.
/// </para>
/// </remarks>
public class PluginKeyService(
    ApiDbContext context,
    IPluginSigningService signing,
    IServerPluginStatusRepository statuses,
    IApiKeyProtector protector,
    TimeProvider clock,
    ILogger<PluginKeyService> logger) : IPluginKeyService
{
    private const int KeySizeBits = 2048;

    public async Task<IReadOnlyList<PluginKeyInfo>> ListAsync()
    {
        var counts = (await statuses.SummarizeByKeyAsync()).ToDictionary(s => s.Fingerprint, StringComparer.OrdinalIgnoreCase);
        var list = new List<PluginKeyInfo>();

        var active = await signing.TryGetFingerprintAsync();
        if (active is not null)
        {
            list.Add(Info(active, PluginKeyState.Active, null, null, null, counts) with { ActiveSinceUtc = await ActiveSinceAsync() });
        }

        var old = await context.PluginKeyHistories.AsNoTracking().OrderByDescending(k => k.RetiredAtUtc).ToListAsync();
        list.AddRange(old.Select(k => Info(k.Fingerprint, k.State, k.RetiredAtUtc, k.RevokedAtUtc, k.RevokedReason, counts)));
        return list;
    }

    public async Task<PluginKeyReminder> GetReminderAsync()
    {
        var days = await context.PlatformSettings.AsNoTracking()
            .Where(s => s.Key == PlatformSettingsRegistry.PluginKeyRotationReminderDays).Select(s => s.Value).FirstOrDefaultAsync();
        var reminderDays = int.TryParse(days, out var parsed) && parsed >= 0 ? parsed : PlatformSettingsRegistry.DefaultPluginKeyRotationReminderDays;

        var fingerprint = await signing.TryGetFingerprintAsync();
        var since = fingerprint is null ? null : await ActiveSinceAsync();
        int? ageDays = since is null ? null : Math.Max(0, (int)(clock.GetUtcNow() - since.Value).TotalDays);
        var due = fingerprint is not null && ageDays is not null && reminderDays > 0 && ageDays >= reminderDays;

        return new PluginKeyReminder(fingerprint, since, ageDays, reminderDays, due);
    }

    // When the active key became the active one - see IPluginKeyService.GetReminderAsync for the order of what is believed.
    private async Task<DateTimeOffset?> ActiveSinceAsync()
    {
        var replaced = await context.PluginKeyHistories.AsNoTracking().Select(k => (DateTimeOffset?)k.RetiredAtUtc).MaxAsync();
        var generated = await context.PluginAdminEvents.AsNoTracking()
            .Where(e => e.Kind == PluginAdminEventKind.KeyGenerated).Select(e => (DateTimeOffset?)e.AtUtc).MaxAsync();

        // A key that was rotated in came after the one it replaced; one that was generated came at its own moment: the later of the two is the active key's.
        if (replaced is not null || generated is not null)
        {
            return replaced > generated || generated is null ? replaced : generated;
        }

        return await context.PlatformSettings.AsNoTracking()
            .Where(s => s.Key == PlatformSettingsRegistry.PluginSigningKey).Select(s => (DateTimeOffset?)s.CreatedOn).FirstOrDefaultAsync();
    }

    public async Task<PluginKeyInfo> RotateAsync(string actor, string? note, bool revokeCurrent = false, string? revokeReason = null)
    {
        if (revokeCurrent && string.IsNullOrWhiteSpace(revokeReason))
        {
            throw new PluginKeyOperationException("reason_required", "Revoking a key needs a reason.");
        }

        await signing.GetPublicKeyAsync(); // make sure there is a key to retire (creates the first on a brand-new Panel)

        var setting = await context.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == PlatformSettingsRegistry.PluginSigningKey)
            ?? throw new PluginKeyOperationException("no_active_key", "The signing key setting does not exist.");
        var oldStored = setting.Value;
        if (string.IsNullOrEmpty(oldStored))
        {
            throw new PluginKeyOperationException("no_active_key", "There is no active key to rotate.");
        }

        var (oldModulus, oldExponent) = PublicHalfOf(oldStored);
        var oldFingerprint = PluginScriptStamper.Fingerprint(oldModulus);

        using var fresh = RSA.Create(KeySizeBits);
        var newStored = protector.Protect(ApiKeyProtectorPurposes.PluginSigningKey, Convert.ToBase64String(fresh.ExportPkcs8PrivateKey()));
        var newParameters = fresh.ExportParameters(false);
        var newFingerprint = PluginScriptStamper.Fingerprint(Convert.ToBase64String(newParameters.Modulus!));

        var now = clock.GetUtcNow();
        await using var transaction = await context.Database.BeginTransactionAsync();

        context.PluginKeyHistories.Add(new PluginKeyHistory
        {
            Fingerprint = oldFingerprint,
            ModulusBase64 = oldModulus,
            ExponentBase64 = oldExponent,
            EncryptedPrivateKey = oldStored,
            State = revokeCurrent ? PluginKeyState.Revoked : PluginKeyState.Retired,
            RetiredAtUtc = now,
            RevokedAtUtc = revokeCurrent ? now : null,
            RevokedReason = revokeCurrent ? revokeReason!.Trim() : null
        });
        context.PluginAdminEvents.Add(new PluginAdminEvent
        {
            AtUtc = now,
            Kind = PluginAdminEventKind.KeyRotated,
            Subject = newFingerprint,
            Actor = actor,
            Detail = Trim($"Replaced {oldFingerprint}{(revokeCurrent ? $" and revoked it: {revokeReason}" : "")}{(string.IsNullOrWhiteSpace(note) ? "" : $". {note}")}")
        });
        if (revokeCurrent)
        {
            context.PluginAdminEvents.Add(new PluginAdminEvent
            {
                AtUtc = now, Kind = PluginAdminEventKind.KeyRevoked, Subject = oldFingerprint, Actor = actor, Detail = Trim(revokeReason)
            });
        }
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another rotation stored this same old key in the history first (its fingerprint is unique): we lost.
            await transaction.RollbackAsync();
            context.ChangeTracker.Clear();
            throw new PluginKeyOperationException("concurrent_change", "The signing key changed while rotating; nothing was changed. Try again.");
        }

        // Compare-and-swap: only replace the key if it is still the one we just read.
        var swapped = await context.PlatformSettings
            .Where(s => s.Key == PlatformSettingsRegistry.PluginSigningKey && s.Value == oldStored)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, newStored));
        if (swapped != 1)
        {
            await transaction.RollbackAsync();
            context.ChangeTracker.Clear();
            throw new PluginKeyOperationException("concurrent_change", "The signing key changed while rotating; nothing was changed. Try again.");
        }

        await transaction.CommitAsync();

        // The swap went straight to the database, past the change tracker. If this context already loaded the setting,
        // the signing service would keep reading the OLD key from that tracked copy for the rest of the request.
        foreach (var entry in context.ChangeTracker.Entries<PlatformSetting>().Where(e => e.Entity.Key == PlatformSettingsRegistry.PluginSigningKey).ToList())
        {
            await entry.ReloadAsync();
        }

        logger.LogWarning("Plugin signing key rotated by {Actor}: {Old} -> {New}{Revoked}.", actor, oldFingerprint, newFingerprint, revokeCurrent ? " (old key revoked)" : "");

        return new PluginKeyInfo(newFingerprint, PluginKeyState.Active, null, null, null, 0, null, now);
    }

    public async Task<PluginKeyInfo> RevokeAsync(string fingerprint, string reason, string actor)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new PluginKeyOperationException("reason_required", "Revoking a key needs a reason.");
        }

        var wanted = (fingerprint ?? "").Trim().ToLowerInvariant();
        var active = await signing.TryGetFingerprintAsync();
        if (active is not null && string.Equals(active, wanted, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginKeyOperationException("is_active", "The active key cannot be revoked. Rotate to a new key (revoking this one) instead.");
        }

        var key = await context.PluginKeyHistories.FirstOrDefaultAsync(k => k.Fingerprint == wanted)
            ?? throw new PluginKeyOperationException("not_found", "No such key.");
        if (key.State == PluginKeyState.Revoked)
        {
            throw new PluginKeyOperationException("already_revoked", "That key is already revoked.");
        }

        var now = clock.GetUtcNow();
        key.State = PluginKeyState.Revoked;
        key.RevokedAtUtc = now;
        key.RevokedReason = Trim(reason.Trim(), 500);
        context.PluginAdminEvents.Add(new PluginAdminEvent
        {
            AtUtc = now, Kind = PluginAdminEventKind.KeyRevoked, Subject = key.Fingerprint, Actor = actor, Detail = Trim(reason)
        });
        await context.SaveChangesAsync();

        logger.LogWarning("Plugin signing key {Fingerprint} revoked by {Actor}: {Reason}", key.Fingerprint, actor, reason);
        var counts = (await statuses.SummarizeByKeyAsync()).ToDictionary(s => s.Fingerprint, StringComparer.OrdinalIgnoreCase);
        return Info(key.Fingerprint, key.State, key.RetiredAtUtc, key.RevokedAtUtc, key.RevokedReason, counts);
    }

    // ---- export and import ---------------------------------------------------------------------------------

    public async Task<PluginKeyExport> ExportAsync(string passphrase, string actor)
    {
        if (!PluginKeyBundle.IsAcceptablePassphrase(passphrase))
        {
            throw new PluginKeyOperationException(
                "passphrase_weak", $"The passphrase must be {PluginKeyBundle.MinPassphraseLength} to {PluginKeyBundle.MaxPassphraseLength} characters.");
        }

        await signing.GetPublicKeyAsync(); // a brand-new Panel has no key until first use: make it, so there is something to back up

        var setting = await context.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == PlatformSettingsRegistry.PluginSigningKey)
            ?? throw new PluginKeyOperationException("no_active_key", "The signing key setting does not exist.");
        if (string.IsNullOrEmpty(setting.Value))
        {
            throw new PluginKeyOperationException("no_active_key", "There is no active key to export.");
        }

        var keys = new List<PluginKeyBundleKey> { ReadStored(setting.Value, PluginKeyState.Active, null, null, null) };
        foreach (var h in await context.PluginKeyHistories.AsNoTracking().OrderBy(k => k.RetiredAtUtc).ToListAsync())
        {
            keys.Add(ReadStored(h.EncryptedPrivateKey, h.State, h.RetiredAtUtc, h.RevokedAtUtc, h.RevokedReason));
        }

        var now = clock.GetUtcNow();
        var json = PluginKeyBundle.Seal(keys, passphrase, now);

        context.PluginAdminEvents.Add(new PluginAdminEvent
        {
            AtUtc = now, Kind = PluginAdminEventKind.KeysExported, Subject = keys[0].Fingerprint, Actor = actor,
            Detail = Trim($"{keys.Count} key(s): {string.Join(", ", keys.Select(k => k.Fingerprint))}")
        });
        await context.SaveChangesAsync();

        logger.LogWarning("Plugin signing keys ({Count}) exported by {Actor}.", keys.Count, actor);
        return new PluginKeyExport(
            $"rustarchon-signing-keys-{keys[0].Fingerprint[..8]}-{now:yyyyMMdd-HHmmss}.json", json, keys.Select(k => k.Fingerprint).ToList());
    }

    public async Task<PluginKeyImportResult> ImportAsync(
        string bundleJson, string passphrase, bool activateBundleKey, bool dryRun, string actor, string? note)
    {
        var bundle = PluginKeyBundle.Open(bundleJson, passphrase);

        var setting = await context.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == PlatformSettingsRegistry.PluginSigningKey)
            ?? throw new PluginKeyOperationException("no_active_key", "The signing key setting does not exist.");
        var activeStored = setting.Value ?? "";
        var activeFingerprint = string.IsNullOrEmpty(activeStored) ? null : FingerprintOfStored(activeStored);
        var history = await context.PluginKeyHistories.ToListAsync();
        var local = history.ToDictionary(h => h.Fingerprint, StringComparer.OrdinalIgnoreCase);

        var bundleActive = bundle.FirstOrDefault(k => k.State == PluginKeyState.Active);
        if (activateBundleKey && bundleActive is null)
        {
            throw new PluginKeyOperationException("no_active_in_bundle", "The file has no active key to make active here.");
        }

        if (activateBundleKey && local.TryGetValue(bundleActive!.Fingerprint, out var revokedHere) && revokedHere.State == PluginKeyState.Revoked)
        {
            throw new PluginKeyOperationException(
                "cannot_activate_revoked", $"Key {bundleActive.Fingerprint} is revoked on this Panel and cannot be made active again.");
        }

        var activating = activateBundleKey && bundleActive is not null
            && !string.Equals(bundleActive.Fingerprint, activeFingerprint, StringComparison.OrdinalIgnoreCase)
            ? bundleActive
            : null;

        var items = new List<PluginKeyImportItem>();
        var toAdd = new List<PluginKeyBundleKey>();
        var toRevoke = new List<(PluginKeyHistory Row, PluginKeyBundleKey Key)>();
        PluginKeyHistory? reactivated = null;

        foreach (var k in bundle)
        {
            PluginKeyState? here = string.Equals(k.Fingerprint, activeFingerprint, StringComparison.OrdinalIgnoreCase)
                ? PluginKeyState.Active
                : local.TryGetValue(k.Fingerprint, out var row) ? row.State : null;

            if (k.State == PluginKeyState.Revoked)
            {
                if (here == PluginKeyState.Active)
                {
                    throw new PluginKeyOperationException(
                        "revoked_in_bundle_active_here",
                        $"Key {k.Fingerprint} is revoked in the file but is the active key here. Rotate to a new key (revoking this one) first, then import.");
                }

                if (here == PluginKeyState.Retired)
                {
                    toRevoke.Add((local[k.Fingerprint], k));
                    items.Add(new PluginKeyImportItem(k.Fingerprint, k.State, "revoked"));
                }
                else if (here is null)
                {
                    toAdd.Add(k);
                    items.Add(new PluginKeyImportItem(k.Fingerprint, k.State, "added"));
                }
                else
                {
                    items.Add(new PluginKeyImportItem(k.Fingerprint, k.State, "already_present"));
                }

                continue;
            }

            if (activating is not null && k == activating)
            {
                if (here == PluginKeyState.Retired) { reactivated = local[k.Fingerprint]; }
                items.Add(new PluginKeyImportItem(k.Fingerprint, k.State, "activated"));
            }
            else if (here == PluginKeyState.Active)
            {
                items.Add(new PluginKeyImportItem(
                    k.Fingerprint, k.State, activating is not null ? "previous_active_retired" : "already_active"));
            }
            else if (here is not null)
            {
                items.Add(new PluginKeyImportItem(k.Fingerprint, k.State, "already_present"));
            }
            else
            {
                toAdd.Add(k);
                items.Add(new PluginKeyImportItem(k.Fingerprint, k.State, "added"));
            }
        }

        // The key that is active here and that the file does not mention still ends up in the history if it is being replaced.
        if (activating is not null && activeFingerprint is not null
            && !bundle.Any(b => string.Equals(b.Fingerprint, activeFingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new PluginKeyImportItem(activeFingerprint, null, "previous_active_retired"));
        }

        var result = new PluginKeyImportResult(dryRun, items, activeFingerprint, activating?.Fingerprint ?? activeFingerprint);
        if (dryRun || !result.ChangesAnything)
        {
            return result;
        }

        var now = clock.GetUtcNow();
        await using var transaction = await context.Database.BeginTransactionAsync();

        foreach (var k in toAdd)
        {
            var (modulus, exponent) = PublicHalfOfPlain(k.Pkcs8Base64);
            var revoked = k.State == PluginKeyState.Revoked;
            context.PluginKeyHistories.Add(new PluginKeyHistory
            {
                Fingerprint = k.Fingerprint,
                ModulusBase64 = modulus,
                ExponentBase64 = exponent,
                EncryptedPrivateKey = protector.Protect(ApiKeyProtectorPurposes.PluginSigningKey, k.Pkcs8Base64),
                State = revoked ? PluginKeyState.Revoked : PluginKeyState.Retired,
                RetiredAtUtc = k.RetiredAtUtc ?? now,
                RevokedAtUtc = revoked ? k.RevokedAtUtc ?? now : null,
                RevokedReason = revoked ? k.RevokedReason ?? "Revoked in an imported bundle." : null
            });
        }

        foreach (var (row, k) in toRevoke)
        {
            row.State = PluginKeyState.Revoked;
            row.RevokedAtUtc = k.RevokedAtUtc ?? now;
            row.RevokedReason = k.RevokedReason ?? "Revoked in an imported bundle.";
        }

        string? newStored = null;
        if (activating is not null)
        {
            if (activeFingerprint is not null)
            {
                var (oldModulus, oldExponent) = PublicHalfOf(activeStored);
                context.PluginKeyHistories.Add(new PluginKeyHistory
                {
                    Fingerprint = activeFingerprint, ModulusBase64 = oldModulus, ExponentBase64 = oldExponent,
                    EncryptedPrivateKey = activeStored, State = PluginKeyState.Retired, RetiredAtUtc = now
                });
            }

            if (reactivated is not null)
            {
                context.PluginKeyHistories.Remove(reactivated); // it is the active key now, so it leaves the history
            }

            newStored = protector.Protect(ApiKeyProtectorPurposes.PluginSigningKey, activating.Pkcs8Base64);
        }

        var added = toAdd.Count;
        context.PluginAdminEvents.Add(new PluginAdminEvent
        {
            AtUtc = now, Kind = PluginAdminEventKind.KeysImported, Subject = result.ActiveAfter ?? "", Actor = actor,
            Detail = Trim(
                $"{added} added, {toRevoke.Count} revoked" + (activating is not null ? $", activated {activating.Fingerprint} (was {activeFingerprint ?? "none"})" : "")
                + (string.IsNullOrWhiteSpace(note) ? "" : $". {note}"))
        });

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            context.ChangeTracker.Clear();
            throw new PluginKeyOperationException("concurrent_change", "The signing keys changed while importing; nothing was changed. Try again.");
        }

        if (newStored is not null)
        {
            // Compare-and-swap, as for a rotation: only replace the active key if it is still the one we read.
            var swapped = await context.PlatformSettings
                .Where(s => s.Key == PlatformSettingsRegistry.PluginSigningKey && s.Value == activeStored)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, newStored));
            if (swapped != 1)
            {
                await transaction.RollbackAsync();
                context.ChangeTracker.Clear();
                throw new PluginKeyOperationException("concurrent_change", "The signing key changed while importing; nothing was changed. Try again.");
            }
        }

        await transaction.CommitAsync();

        foreach (var entry in context.ChangeTracker.Entries<PlatformSetting>().Where(e => e.Entity.Key == PlatformSettingsRegistry.PluginSigningKey).ToList())
        {
            await entry.ReloadAsync();
        }

        logger.LogWarning(
            "Plugin signing keys imported by {Actor}: {Added} added, {Revoked} revoked{Activated}.",
            actor, added, toRevoke.Count, activating is not null ? $", active key now {activating.Fingerprint}" : "");
        return result;
    }

    // A stored key, decrypted and described for a bundle. An unreadable one stops the whole export: leaving it out would look like a backup and not be one.
    private PluginKeyBundleKey ReadStored(string stored, PluginKeyState state, DateTimeOffset? retired, DateTimeOffset? revoked, string? reason)
    {
        try
        {
            var pkcs8 = protector.Unprotect(ApiKeyProtectorPurposes.PluginSigningKey, stored);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8), out _);
            return new PluginKeyBundleKey(PluginKeyBundle.FingerprintOf(rsa), pkcs8, state, retired, revoked, reason);
        }
        catch (Exception ex) when (ex is not PluginKeyOperationException)
        {
            throw new PluginKeyOperationException(
                "key_unreadable", "A stored signing key cannot be read, so nothing was exported: " + ex.GetType().Name);
        }
    }

    private string FingerprintOfStored(string stored)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(ApiKeyProtectorPurposes.PluginSigningKey, stored)), out _);
            return PluginKeyBundle.FingerprintOf(rsa);
        }
        catch (Exception ex)
        {
            throw new PluginKeyOperationException("key_unreadable", "The stored signing key cannot be read: " + ex.GetType().Name);
        }
    }

    private static (string Modulus, string Exponent) PublicHalfOfPlain(string pkcs8Base64)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64), out _);
        var p = rsa.ExportParameters(false);
        return (Convert.ToBase64String(p.Modulus!), Convert.ToBase64String(p.Exponent!));
    }

    private static PluginKeyInfo Info(
        string fingerprint, PluginKeyState state, DateTimeOffset? retired, DateTimeOffset? revoked, string? reason,
        Dictionary<string, KeyReportSummary> counts)
    {
        counts.TryGetValue(fingerprint, out var seen);
        return new PluginKeyInfo(fingerprint, state, retired, revoked, reason, seen?.Servers ?? 0, seen?.LastReportedUtc);
    }

    // The public modulus and exponent of a stored (encrypted) private key.
    private (string Modulus, string Exponent) PublicHalfOf(string stored)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(ApiKeyProtectorPurposes.PluginSigningKey, stored)), out _);
            var p = rsa.ExportParameters(false);
            return (Convert.ToBase64String(p.Modulus!), Convert.ToBase64String(p.Exponent!));
        }
        catch (Exception ex)
        {
            throw new PluginKeyOperationException("key_unreadable", "The stored signing key cannot be read, so it will not be replaced: " + ex.GetType().Name);
        }
    }

    private static string Trim(string? text, int max = 1000) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];
}
