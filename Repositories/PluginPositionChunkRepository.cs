// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Repositories;

/// <summary>Stores the player positions the Worker drains from a server's plugin, and reads them back for the Panel.</summary>
public interface IPluginPositionChunkRepository : IRepository<PluginPositionChunk>
{
    /// <summary>
    /// Stores the samples of one batch that are not already stored, as one chunk. Returns how many samples were new (0 when
    /// the whole batch was a repeat). Runs across all tenants: its caller is a message consumer with no ambient tenant.
    /// </summary>
    /// <exception cref="PositionChunkCodec.InvalidPositionBatchException">The batch is malformed; nothing is stored.</exception>
    Task<int> AppendAsync(Guid tenantId, Guid rustServerId, long bootId, bool precededByGap, string samplesJson, DateTimeOffset now);

    /// <summary>
    /// Position samples for one server, newest first, optionally limited to a time window and to one player. Tenant-filtered
    /// like every ordinary read, so another tenant's server yields nothing.
    /// </summary>
    Task<PositionsDto> QueryAsync(Guid rustServerId, DateTimeOffset? since, DateTimeOffset? until, string? playerId, int limit);
}

/// <inheritdoc cref="IPluginPositionChunkRepository" />
public class PluginPositionChunkRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginPositionChunk>(context, userContext), IPluginPositionChunkRepository
{
    /// <summary>Most chunks opened for one page, so a filter that matches almost nothing cannot scan a server's whole history.</summary>
    public const int MaxChunksScanned = 400;

    private const int ChunkPageSize = 25;

    public async Task<int> AppendAsync(Guid tenantId, Guid rustServerId, long bootId, bool precededByGap, string samplesJson, DateTimeOffset now)
    {
        var samples = PositionChunkCodec.Parse(samplesJson);
        if (samples.Count == 0)
        {
            return 0;
        }

        // Drop what an earlier chunk of this same boot already covers (a batch sent twice, or again after a Worker restart),
        // judged by stored sequence RANGES so two batches handled out of order can never make the earlier one look like a repeat.
        var first = samples[0].Sequence;
        var last = samples[^1].Sequence;
        var covered = await _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(c => c.RustServerId == rustServerId && c.BootId == bootId && c.LastSequence >= first && c.FirstSequence <= last)
            .Select(c => new { c.FirstSequence, c.LastSequence })
            .ToListAsync();

        var fresh = samples.Where(s => !covered.Any(r => s.Sequence >= r.FirstSequence && s.Sequence <= r.LastSequence)).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        var chunk = new PluginPositionChunk
        {
            TenantId = tenantId,
            RustServerId = rustServerId,
            BootId = bootId,
            FirstSequence = fresh[0].Sequence,
            LastSequence = fresh[^1].Sequence,
            SampleCount = fresh.Count,
            FromUtc = fresh.Min(s => s.OccurredAtUtc),
            ToUtc = fresh.Max(s => s.OccurredAtUtc),
            Format = PositionChunkCodec.SupportedFormat,
            Data = PositionChunkCodec.Compress(fresh),
            PlayerIds = fresh.Select(s => s.PlayerId).Distinct().ToArray(),
            PrecededByGap = precededByGap,
            CreatedAtUtc = now
        };

        try
        {
            await _dbSet.AddAsync(chunk);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The same batch arrived twice at the same moment and the other one won the unique index. Not an error.
            _context.ChangeTracker.Clear();
            return 0;
        }

        return fresh.Count;
    }

    public async Task<PositionsDto> QueryAsync(Guid rustServerId, DateTimeOffset? since, DateTimeOffset? until, string? playerId, int limit)
    {
        limit = Math.Clamp(limit, 1, 5000);

        var query = _dbSet.AsNoTracking().Where(c => c.RustServerId == rustServerId);
        if (since is not null) { query = query.Where(c => c.ToUtc >= since); }
        if (until is not null) { query = query.Where(c => c.FromUtc <= until); }
        if (!string.IsNullOrEmpty(playerId)) { query = query.Where(c => c.PlayerIds.Contains(playerId)); }
        query = query.OrderByDescending(c => c.ToUtc).ThenByDescending(c => c.FirstSequence);

        var collected = new List<PositionSampleDto>();
        var scanned = 0;
        while (scanned < MaxChunksScanned)
        {
            var page = await query.Skip(scanned).Take(ChunkPageSize).ToListAsync();
            if (page.Count == 0)
            {
                break;
            }

            foreach (var chunk in page)
            {
                foreach (var s in PositionChunkCodec.Decode(chunk.Data, chunk.Format))
                {
                    if (since is not null && s.OccurredAtUtc < since) { continue; }
                    if (until is not null && s.OccurredAtUtc > until) { continue; }
                    if (!string.IsNullOrEmpty(playerId) && s.PlayerId != playerId) { continue; }

                    collected.Add(s);
                }
            }

            scanned += page.Count;
            collected = collected.OrderByDescending(s => s.OccurredAtUtc).ThenByDescending(s => s.Sequence).ToList();

            // Every chunk not yet opened ends no later than the last one just opened, so once the (limit+1)th newest sample
            // is at least that recent nothing unread can displace it.
            if (collected.Count > limit && collected[limit].OccurredAtUtc >= page[^1].ToUtc)
            {
                break;
            }
        }

        return new PositionsDto { Samples = collected.Take(limit).ToList(), HasMore = collected.Count > limit };
    }
}
