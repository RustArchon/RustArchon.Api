// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Repositories;

/// <summary>Stores and reads a server's current tool cupboard snapshot.</summary>
public interface IPluginTcSnapshotRepository : IRepository<PluginTcSnapshot>
{
    /// <summary>
    /// Replaces the server's snapshot with this one - unless a newer one is already stored (two reads handled out of order
    /// must not roll it back). Runs across all tenants: its caller is a message consumer with no ambient tenant.
    /// </summary>
    /// <returns><c>true</c> if this snapshot is now the stored one.</returns>
    /// <exception cref="TcSnapshotCodec.InvalidTcSnapshotException">The list is malformed; nothing is stored.</exception>
    Task<bool> ReplaceAsync(Guid tenantId, Guid rustServerId, bool ready, string tcsJson, DateTimeOffset capturedAtUtc);

    /// <summary>The server's bases as last read, or an empty, not-ready picture if nothing has been read. Tenant-filtered.</summary>
    Task<BasesDto> GetAsync(Guid rustServerId);
}

/// <inheritdoc cref="IPluginTcSnapshotRepository" />
public class PluginTcSnapshotRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginTcSnapshot>(context, userContext), IPluginTcSnapshotRepository
{
    public async Task<bool> ReplaceAsync(Guid tenantId, Guid rustServerId, bool ready, string tcsJson, DateTimeOffset capturedAtUtc)
    {
        var tcs = TcSnapshotCodec.Parse(tcsJson);
        var data = TcSnapshotCodec.Compress(tcs);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await _dbSet.AcrossAllTenants().FirstOrDefaultAsync(s => s.RustServerId == rustServerId);
            if (existing is null)
            {
                try
                {
                    await _dbSet.AddAsync(new PluginTcSnapshot
                    {
                        TenantId = tenantId, RustServerId = rustServerId, Ready = ready, Count = tcs.Count,
                        Format = TcSnapshotCodec.SupportedFormat, Data = data, CapturedAtUtc = capturedAtUtc
                    });
                    await _context.SaveChangesAsync();
                    return true;
                }
                catch (DbUpdateException)
                {
                    // Two reads of a brand-new server landed together and the other one inserted first: update it instead.
                    _context.ChangeTracker.Clear();
                    continue;
                }
            }

            if (existing.TenantId != tenantId || existing.CapturedAtUtc > capturedAtUtc)
            {
                return false; // someone else's row (never overwritten by a message claiming another tenant), or a newer one is stored
            }

            existing.Ready = ready;
            existing.Count = tcs.Count;
            existing.Format = TcSnapshotCodec.SupportedFormat;
            existing.Data = data;
            existing.CapturedAtUtc = capturedAtUtc;
            await _context.SaveChangesAsync();
            return true;
        }

        return false;
    }

    public async Task<BasesDto> GetAsync(Guid rustServerId)
    {
        var snapshot = await _dbSet.AsNoTracking().FirstOrDefaultAsync(s => s.RustServerId == rustServerId);
        if (snapshot is null)
        {
            return new BasesDto { Ready = false, CapturedAtUtc = null, Tcs = [] };
        }

        return new BasesDto
        {
            Ready = snapshot.Ready,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            Tcs = TcSnapshotCodec.Decode(snapshot.Data, snapshot.Format)
        };
    }
}
