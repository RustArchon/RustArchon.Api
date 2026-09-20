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

/// <summary>Keeps what is known about each server's world map, and hands out the one-time tokens for collecting the pictures.</summary>
public interface IPluginMapRepository : IRepository<PluginMap>
{
    /// <summary>
    /// Records that the Worker saw this world, creating its row on first sight. <paramref name="monumentsJson"/> replaces the
    /// stored list only when given. Runs across all tenants: its caller is a message consumer with no ambient tenant.
    /// </summary>
    Task<PluginMap> UpsertStatusAsync(
        Guid tenantId, Guid rustServerId, int worldSize, long worldSeed, string fileName, bool existsOnServer, long serverBytes,
        string? monumentsJson, DateTimeOffset now);

    /// <summary>A map row by id, across tenants (the ingest door has no ambient tenant).</summary>
    Task<PluginMap?> GetByIdAsync(Guid id);

    /// <summary>The server's current map: the world the Worker reported most recently. Tenant-filtered like every ordinary read.</summary>
    Task<PluginMap?> GetCurrentAsync(Guid rustServerId);

    /// <summary>
    /// Claims the right to ask the server for the picture: true for exactly one caller when none has asked within
    /// <paramref name="cooldown"/>. The atomic gate that keeps a failing upload from being retried on every poll.
    /// </summary>
    Task<bool> TryClaimUploadRequestAsync(Guid id, DateTimeOffset now, TimeSpan cooldown);

    /// <summary>Records that the picture is stored.</summary>
    Task RecordUploadAsync(Guid id, long bytes, string sha256, string objectKey, DateTimeOffset now);

    /// <summary>Records that the display-sized preview of the picture is stored.</summary>
    Task RecordPreviewAsync(Guid id, string objectKey, long bytes, string sha256);
}

/// <inheritdoc cref="IPluginMapRepository" />
public class PluginMapRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginMap>(context, userContext), IPluginMapRepository
{
    public async Task<PluginMap> UpsertStatusAsync(
        Guid tenantId, Guid rustServerId, int worldSize, long worldSeed, string fileName, bool existsOnServer, long serverBytes,
        string? monumentsJson, DateTimeOffset now)
    {
        // Two attempts: the second covers the same world being reported twice at the same moment, where the loser of the
        // unique index simply finds the winner's row.
        for (var attempt = 0; ; attempt++)
        {
            var row = await _dbSet.AcrossAllTenants()
                .FirstOrDefaultAsync(m => m.RustServerId == rustServerId && m.WorldSize == worldSize && m.WorldSeed == worldSeed);

            if (row is null)
            {
                row = new PluginMap { TenantId = tenantId, RustServerId = rustServerId, WorldSize = worldSize, WorldSeed = worldSeed };
                await _dbSet.AddAsync(row);
            }
            else if (row.TenantId != tenantId)
            {
                // A row for this server under another organization would mean the message lies about who owns the server.
                throw new InvalidOperationException("A map row for this server exists under a different organization.");
            }

            row.FileName = fileName.Length <= 100 ? fileName : fileName[..100];
            row.ExistsOnServer = existsOnServer;
            row.ServerBytes = serverBytes;
            row.LastSeenUtc = now;
            if (monumentsJson is not null)
            {
                row.MonumentsJson = monumentsJson;
            }

            try
            {
                await _context.SaveChangesAsync();
                return row;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    public Task<PluginMap?> GetByIdAsync(Guid id) =>
        _dbSet.AcrossAllTenants().AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);

    public Task<PluginMap?> GetCurrentAsync(Guid rustServerId) =>
        _dbSet.AsNoTracking().Where(m => m.RustServerId == rustServerId).OrderByDescending(m => m.LastSeenUtc).FirstOrDefaultAsync();

    public async Task<bool> TryClaimUploadRequestAsync(Guid id, DateTimeOffset now, TimeSpan cooldown)
    {
        var earliest = now - cooldown;
        var changed = await _dbSet.AcrossAllTenants()
            .Where(m => m.Id == id && (m.UploadRequestedAtUtc == null || m.UploadRequestedAtUtc < earliest))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.UploadRequestedAtUtc, now));
        return changed == 1;
    }

    public async Task RecordUploadAsync(Guid id, long bytes, string sha256, string objectKey, DateTimeOffset now) =>
        await _dbSet.AcrossAllTenants().Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.UploadedAtUtc, now)
            .SetProperty(m => m.UploadedBytes, bytes)
            .SetProperty(m => m.Sha256, sha256)
            .SetProperty(m => m.ObjectKey, objectKey)
            // A new picture invalidates the preview of the old one; it is made again from this one.
            .SetProperty(m => m.PreviewObjectKey, (string?)null)
            .SetProperty(m => m.PreviewBytes, (long?)null)
            .SetProperty(m => m.PreviewSha256, (string?)null));

    public async Task RecordPreviewAsync(Guid id, string objectKey, long bytes, string sha256) =>
        await _dbSet.AcrossAllTenants().Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.PreviewObjectKey, objectKey)
            .SetProperty(m => m.PreviewBytes, bytes)
            .SetProperty(m => m.PreviewSha256, sha256));
}

/// <summary>What a redeemed map upload token entitles its holder to: completing one map row of one server.</summary>
public sealed record PluginMapUploadRedemption(Guid PluginMapId, Guid RustServerId);

/// <summary>Mints and redeems the single-use tokens a map upload is handed.</summary>
public interface IPluginMapUploadTokenRepository : IRepository<PluginMapUploadToken>
{
    /// <summary>Creates a token for one server and one map row and returns it. The raw token is returned only here.</summary>
    Task<string> MintAsync(Guid tenantId, Guid rustServerId, Guid pluginMapId, TimeSpan lifetime);

    /// <summary>
    /// Uses a token: exactly once, before it expires. The token alone says which server and which map row it is for (it was
    /// minted for that pair and nothing else), so the caller supplies no server id to be checked against. Anything else
    /// (unknown, already used, expired) is a plain null that says nothing about which.
    /// </summary>
    Task<PluginMapUploadRedemption?> RedeemAsync(string rawToken);

    /// <summary>Discards a token that was minted but not used (the plugin refused the request).</summary>
    Task RevokeAsync(string rawToken);
}

/// <inheritdoc cref="IPluginMapUploadTokenRepository" />
public class PluginMapUploadTokenRepository(ApiDbContext context, TimeProvider clock, IUserContext? userContext = null)
    : Repository<PluginMapUploadToken>(context, userContext), IPluginMapUploadTokenRepository
{
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public async Task<string> MintAsync(Guid tenantId, Guid rustServerId, Guid pluginMapId, TimeSpan lifetime)
    {
        var now = clock.GetUtcNow();
        var raw = NewToken();

        await _dbSet.AddAsync(new PluginMapUploadToken
        {
            TenantId = tenantId,
            RustServerId = rustServerId,
            PluginMapId = pluginMapId,
            TokenHash = HashToken(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + lifetime
        });
        await _context.SaveChangesAsync();

        // Housekeeping: nothing reads a token more than a day past its expiry.
        var cutoff = now.AddDays(-1);
        await _dbSet.AcrossAllTenants().Where(t => t.ExpiresAtUtc < cutoff).ExecuteDeleteAsync();

        return raw;
    }

    public async Task<PluginMapUploadRedemption?> RedeemAsync(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return null;
        }

        var hash = HashToken(rawToken);
        var now = clock.GetUtcNow();

        var changed = await _dbSet.AcrossAllTenants()
            .Where(t => t.TokenHash == hash && t.RedeemedAtUtc == null && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RedeemedAtUtc, now));

        if (changed != 1)
        {
            return null;
        }

        // The row is ours now (redeemed, so nobody else can), and what it names never changes: read it back.
        return await _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new PluginMapUploadRedemption(t.PluginMapId, t.RustServerId))
            .FirstOrDefaultAsync();
    }

    public async Task RevokeAsync(string rawToken)
    {
        var hash = HashToken(rawToken);
        await _dbSet.AcrossAllTenants().Where(t => t.TokenHash == hash).ExecuteDeleteAsync();
    }
}
