// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>What a redeemed token entitles its holder to.</summary>
/// <param name="SigningKeyFingerprint">
/// The key the server's installed plugin trusts (as reported when the token was minted): the download is signed with
/// it, as a bridge to the active key when they differ.
/// </param>
public sealed record PluginTokenRedemption(string SigningKeyFingerprint, Guid RustServerId = default, string Purpose = PluginUpdateTokenPurposes.Main);

/// <summary>Mints and redeems the single-use tokens a plugin update hands the game server.</summary>
public interface IPluginUpdateTokenRepository : IRepository<PluginUpdateToken>
{
    /// <summary>
    /// Creates a token for one server and returns it. The raw token is returned only here; the database keeps a hash.
    /// </summary>
    Task<string> MintAsync(Guid tenantId, Guid rustServerId, string signingKeyFingerprint, TimeSpan lifetime);

    /// <summary>The same for a token that downloads something other than the main plugin - see <see cref="PluginUpdateTokenPurposes"/>.</summary>
    Task<string> MintAsync(Guid tenantId, Guid rustServerId, string signingKeyFingerprint, TimeSpan lifetime, string purpose);

    /// <summary>
    /// Uses a token: a redemption exactly once, and only for the server it was minted for, before it expires. Anything
    /// else (unknown, wrong server, already used, expired) is a plain <c>null</c> that says nothing about which.
    /// </summary>
    Task<PluginTokenRedemption?> RedeemAsync(Guid rustServerId, string rawToken);

    /// <summary>
    /// Uses a token that arrived without a server named (in a header, from Updater 0.3.0): the token itself says which server it was
    /// minted for. Exactly once, before it expires; anything else is a plain <c>null</c>.
    /// </summary>
    Task<PluginTokenRedemption?> RedeemAsync(string rawToken);

    /// <summary>Discards a token that was minted but not used (the plugin refused the request).</summary>
    Task RevokeAsync(string rawToken);
}

/// <inheritdoc cref="IPluginUpdateTokenRepository" />
/// <remarks>
/// Runs across all tenants and by explicit server id: its callers are the anonymous ingest door and a Panel-triggered
/// update, neither of which has the ambient tenant a normal request does (the same reason as
/// <see cref="ServerPluginStatusRepository"/>).
/// </remarks>
public class PluginUpdateTokenRepository(ApiDbContext context, TimeProvider clock, IUserContext? userContext = null)
    : Repository<PluginUpdateToken>(context, userContext), IPluginUpdateTokenRepository
{
    /// <summary>256 random bits, URL-safe. Not derived from anything, so it cannot be guessed from a server id.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Lower-case hex SHA-256 of the token text.</summary>
    public static string HashToken(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public Task<string> MintAsync(Guid tenantId, Guid rustServerId, string signingKeyFingerprint, TimeSpan lifetime) =>
        MintAsync(tenantId, rustServerId, signingKeyFingerprint, lifetime, PluginUpdateTokenPurposes.Main);

    public async Task<string> MintAsync(Guid tenantId, Guid rustServerId, string signingKeyFingerprint, TimeSpan lifetime, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(signingKeyFingerprint);
        if (purpose is not (PluginUpdateTokenPurposes.Main or PluginUpdateTokenPurposes.Updater))
        {
            throw new ArgumentException("Unknown token purpose.", nameof(purpose));
        }

        var now = clock.GetUtcNow();
        var raw = NewToken();

        await _dbSet.AddAsync(new PluginUpdateToken
        {
            TenantId = tenantId,
            RustServerId = rustServerId,
            SigningKeyFingerprint = signingKeyFingerprint,
            Purpose = purpose,
            TokenHash = HashToken(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + lifetime
        });
        await _context.SaveChangesAsync();

        // Housekeeping: nothing reads a token more than a day past its expiry, so drop them.
        var cutoff = now.AddDays(-1);
        await _dbSet.AcrossAllTenants().Where(t => t.ExpiresAtUtc < cutoff).ExecuteDeleteAsync();

        return raw;
    }

    public Task<PluginTokenRedemption?> RedeemAsync(Guid rustServerId, string rawToken) => RedeemCoreAsync(rustServerId, rawToken);

    public Task<PluginTokenRedemption?> RedeemAsync(string rawToken) => RedeemCoreAsync(null, rawToken);

    // rustServerId is null when the caller does not name a server: the token alone then identifies it.
    private async Task<PluginTokenRedemption?> RedeemCoreAsync(Guid? rustServerId, string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return null;
        }

        var hash = HashToken(rawToken);
        var now = clock.GetUtcNow();

        // One statement, so of any number of simultaneous requests carrying this token exactly one changes a row.
        var changed = await _dbSet.AcrossAllTenants()
            .Where(t => t.TokenHash == hash && (rustServerId == null || t.RustServerId == rustServerId) && t.RedeemedAtUtc == null && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RedeemedAtUtc, now));

        if (changed != 1)
        {
            return null;
        }

        // The row is ours now (redeemed, so nobody else can), and its fingerprint never changes: read it back.
        var row = await _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(t => t.TokenHash == hash && (rustServerId == null || t.RustServerId == rustServerId))
            .Select(t => new { t.SigningKeyFingerprint, t.RustServerId, t.Purpose })
            .FirstOrDefaultAsync();

        return row is null || string.IsNullOrEmpty(row.SigningKeyFingerprint) ? null : new PluginTokenRedemption(row.SigningKeyFingerprint, row.RustServerId, row.Purpose);
    }

    public async Task RevokeAsync(string rawToken)
    {
        var hash = HashToken(rawToken);
        await _dbSet.AcrossAllTenants().Where(t => t.TokenHash == hash).ExecuteDeleteAsync();
    }
}
