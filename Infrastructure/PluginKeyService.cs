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
public sealed record PluginKeyInfo(
    string Fingerprint,
    PluginKeyState State,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedReason,
    int ServersReporting,
    DateTimeOffset? LastReportedUtc);

/// <summary>Rotating and revoking the plugin signing key, and listing them all. Site Admin actions, all audit-logged.</summary>
public interface IPluginKeyService
{
    /// <summary>The active key first, then every retired or revoked key, most recently retired first.</summary>
    Task<IReadOnlyList<PluginKeyInfo>> ListAsync();

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
            list.Add(Info(active, PluginKeyState.Active, null, null, null, counts));
        }

        var old = await context.PluginKeyHistories.AsNoTracking().OrderByDescending(k => k.RetiredAtUtc).ToListAsync();
        list.AddRange(old.Select(k => Info(k.Fingerprint, k.State, k.RetiredAtUtc, k.RevokedAtUtc, k.RevokedReason, counts)));
        return list;
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

        return new PluginKeyInfo(newFingerprint, PluginKeyState.Active, null, null, null, 0, null);
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
