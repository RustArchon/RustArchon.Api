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

/// <summary>Stores the combat events the Worker drains from a server's plugin, and reads them back for the Panel.</summary>
public interface IPluginCombatChunkRepository : IRepository<PluginCombatChunk>
{
    /// <summary>
    /// Stores the events of one batch that are not already stored, as one chunk. Returns how many events were new (0 when
    /// the whole batch was a repeat). Runs across all tenants: its caller is a message consumer with no ambient tenant.
    /// </summary>
    /// <exception cref="CombatChunkCodec.InvalidCombatBatchException">The batch is malformed; nothing is stored.</exception>
    Task<int> AppendAsync(Guid tenantId, Guid rustServerId, long bootId, bool precededByGap, string eventsJson, DateTimeOffset now);

    /// <summary>
    /// Combat events for one server, newest first, optionally limited to a time window and to one player (as attacker or
    /// victim). Tenant-filtered like every ordinary read, so another tenant's server yields nothing.
    /// </summary>
    Task<CombatLogDto> QueryAsync(Guid rustServerId, DateTimeOffset? since, DateTimeOffset? until, string? playerId, int limit);
}

/// <inheritdoc cref="IPluginCombatChunkRepository" />
public class PluginCombatChunkRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<PluginCombatChunk>(context, userContext), IPluginCombatChunkRepository
{
    /// <summary>Most chunks opened for one page, so a filter that matches almost nothing cannot scan a server's whole history.</summary>
    public const int MaxChunksScanned = 400;

    private const int ChunkPageSize = 25;

    public async Task<int> AppendAsync(Guid tenantId, Guid rustServerId, long bootId, bool precededByGap, string eventsJson, DateTimeOffset now)
    {
        var events = CombatChunkCodec.Parse(eventsJson);
        if (events.Count == 0)
        {
            return 0;
        }

        // Drop what an earlier chunk of this same boot already covers (a batch sent twice, or again after a Worker
        // restart). Judged by stored sequence RANGES rather than "newer than the newest stored", so two batches handled
        // out of order can never make the earlier one look like a repeat.
        var first = events[0].Sequence;
        var last = events[^1].Sequence;
        var covered = await _dbSet.AcrossAllTenants().AsNoTracking()
            .Where(c => c.RustServerId == rustServerId && c.BootId == bootId && c.LastSequence >= first && c.FirstSequence <= last)
            .Select(c => new { c.FirstSequence, c.LastSequence })
            .ToListAsync();

        var fresh = events.Where(e => !covered.Any(r => e.Sequence >= r.FirstSequence && e.Sequence <= r.LastSequence)).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        var chunk = new PluginCombatChunk
        {
            TenantId = tenantId,
            RustServerId = rustServerId,
            BootId = bootId,
            FirstSequence = fresh[0].Sequence,
            LastSequence = fresh[^1].Sequence,
            EventCount = fresh.Count,
            FromUtc = fresh.Min(e => e.OccurredAtUtc),
            ToUtc = fresh.Max(e => e.OccurredAtUtc),
            Format = CombatChunkCodec.SupportedFormat,
            Data = CombatChunkCodec.Compress(fresh),
            PlayerIds = fresh.SelectMany(e => new[] { e.AttackerPlayerId, e.VictimPlayerId }).Where(id => id is not null).Select(id => id!).Distinct().ToArray(),
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

    public async Task<CombatLogDto> QueryAsync(Guid rustServerId, DateTimeOffset? since, DateTimeOffset? until, string? playerId, int limit)
    {
        limit = Math.Clamp(limit, 1, 500);

        var query = _dbSet.AsNoTracking().Where(c => c.RustServerId == rustServerId);
        if (since is not null) { query = query.Where(c => c.ToUtc >= since); }
        if (until is not null) { query = query.Where(c => c.FromUtc <= until); }
        if (!string.IsNullOrEmpty(playerId)) { query = query.Where(c => c.PlayerIds.Contains(playerId)); }
        query = query.OrderByDescending(c => c.ToUtc).ThenByDescending(c => c.FirstSequence);

        var collected = new List<CombatEventDto>();
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
                foreach (var e in CombatChunkCodec.Decode(chunk.Data, chunk.Format))
                {
                    if (since is not null && e.OccurredAtUtc < since) { continue; }
                    if (until is not null && e.OccurredAtUtc > until) { continue; }
                    if (!string.IsNullOrEmpty(playerId)
                        && !((e.AttackerIsPlayer && e.AttackerId == playerId) || (e.VictimIsPlayer && e.VictimId == playerId)))
                    {
                        continue;
                    }

                    collected.Add(e);
                }
            }

            scanned += page.Count;
            collected = collected.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Sequence).ToList();

            // Every chunk not yet opened ends no later than the last one just opened, so once the (limit+1)th newest
            // event is at least that recent nothing unread can displace it.
            if (collected.Count > limit && collected[limit].OccurredAtUtc >= page[^1].ToUtc)
            {
                break;
            }
        }

        return new CombatLogDto { Events = collected.Take(limit).ToList(), HasMore = collected.Count > limit };
    }
}
