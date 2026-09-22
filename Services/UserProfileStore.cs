// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Services;

/// <summary>Reads and writes people's own settings (<see cref="UserProfile"/>).</summary>
public interface IUserProfileStore
{
    /// <summary>The person's profile, or <c>null</c> if they have none (every setting is then at its default).</summary>
    Task<UserProfile?> FindAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The profiles that exist among these people, by user id. A person with none is simply absent.</summary>
    Task<Dictionary<Guid, UserProfile>> FindManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>Sets the person's language (<c>null</c> clears it), creating the profile if there is none. Safe if two callers do it at once.</summary>
    Task<UserProfile> SetPreferredCultureAsync(Guid userId, string? culture, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records languages held elsewhere (the identity database) for people who have no profile yet. Never overwrites a profile that exists: whatever it says
    /// is newer than what is being handed over. Returns how many were created.
    /// </summary>
    Task<int> BackfillPreferredCulturesAsync(IReadOnlyCollection<(Guid UserId, string? Culture)> items, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public partial class UserProfileStore(ApiDbContext context, TimeProvider clock) : IUserProfileStore
{
    /// <summary>The longest culture name kept (<c>UserProfile.PreferredCulture</c>'s column).</summary>
    public const int MaxCultureLength = 35;

    /// <summary>How many times a write that lost a race to create the same profile is retried before giving up.</summary>
    private const int MaxAttempts = 5;

    /// <summary>A culture name as this platform accepts it: a language, optionally followed by a script, region or variant (<c>en</c>, <c>en-US</c>, <c>zh-Hant-TW</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$")]
    public static partial Regex CultureName();

    /// <summary>The culture as it is stored: trimmed, and <c>null</c> for blank. A name that is not a culture name is refused (<see cref="ArgumentException"/>) rather than stored.</summary>
    public static string? Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return null;
        }

        var trimmed = culture.Trim();
        if (trimmed.Length > MaxCultureLength || !CultureName().IsMatch(trimmed))
        {
            throw new ArgumentException("That is not a culture name such as en-US.", nameof(culture));
        }

        return trimmed;
    }

    /// <summary>Like <see cref="Normalize"/>, but a name that is not a culture name becomes <c>null</c> instead of an error - for what is handed over from elsewhere in bulk.</summary>
    public static string? NormalizeOrNull(string? culture)
    {
        try
        {
            return Normalize(culture);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public Task<UserProfile?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
        context.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

    public async Task<Dictionary<Guid, UserProfile>> FindManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var ids = userIds.Distinct().ToList();
        return await context.UserProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancellationToken);
    }

    public async Task<UserProfile> SetPreferredCultureAsync(Guid userId, string? culture, CancellationToken cancellationToken = default)
    {
        var value = Normalize(culture);

        for (var attempt = 0; ; attempt++)
        {
            var existing = await context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (existing is null)
            {
                existing = new UserProfile { Id = Guid.NewGuid(), UserId = userId };
                context.UserProfiles.Add(existing);
            }

            existing.PreferredCulture = value;
            existing.UpdatedOn = clock.GetUtcNow();

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                context.Entry(existing).State = EntityState.Detached;
                return existing;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts - 1)
            {
                // Someone else created this person's profile between the read and the write: theirs is the row, and ours is applied to it. More than one
                // other caller can be doing the same at once, so this may take a few goes; it is never more than one row.
                context.ChangeTracker.Clear();
            }
        }
    }

    public async Task<int> BackfillPreferredCulturesAsync(IReadOnlyCollection<(Guid UserId, string? Culture)> items, CancellationToken cancellationToken = default)
    {
        var wanted = items.GroupBy(i => i.UserId).Select(g => g.Last()).ToList();
        if (wanted.Count == 0)
        {
            return 0;
        }

        for (var attempt = 0; ; attempt++)
        {
            var ids = wanted.Select(w => w.UserId).ToList();
            var have = (await context.UserProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).Select(p => p.UserId).ToListAsync(cancellationToken)).ToHashSet();
            var created = 0;
            foreach (var (userId, culture) in wanted.Where(w => !have.Contains(w.UserId)))
            {
                context.UserProfiles.Add(new UserProfile { Id = Guid.NewGuid(), UserId = userId, PreferredCulture = NormalizeOrNull(culture), UpdatedOn = clock.GetUtcNow() });
                created++;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                context.ChangeTracker.Clear();
                return created;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts - 1)
            {
                // A profile appeared for one of these people in the meantime (several callers can hand over the same people at once); redo the "who has none" check.
                context.ChangeTracker.Clear();
            }
        }
    }
}
