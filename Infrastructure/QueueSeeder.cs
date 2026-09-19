// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Seeds three starter <see cref="Queue"/> rows on a fresh deployment - Support (the default intake
/// queue), Pre-Sales, and Bug Reports - matching the categories the marketing site's contact form
/// already offers. A self-hoster is free to rename, reorder, deactivate, or add to these.
/// </summary>
/// <remarks>
/// <strong>Idempotent</strong>, safe to call on every Api startup (mirrors <see cref="PlanSeeder"/>) -
/// gated on "does any Queue exist at all," so an admin's later edits are never overwritten by a later
/// restart re-running this.
/// </remarks>
public static class QueueSeeder
{
    /// <summary>The default queue's stable key - see <see cref="Data.Queue.Slug"/>.</summary>
    public const string SupportSlug = "support";

    public const string PreSalesSlug = "pre-sales";

    public const string BugReportsSlug = "bug-reports";

    public static async Task EnsureDefaultsAsync(ApiDbContext dbContext, ILogger logger)
    {
        if (await dbContext.Set<Queue>().AnyAsync())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        dbContext.Set<Queue>().AddRange(
            BuildQueue(SupportSlug, "Support", "General support requests.", isDefault: true, order: 10, now),
            BuildQueue(PreSalesSlug, "Pre-Sales", "Questions from prospects, including invitation requests.", isDefault: false, order: 20, now),
            BuildQueue(BugReportsSlug, "Bug Reports", "Something isn't working as expected.", isDefault: false, order: 30, now));

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Seeded the three starter Queue rows (Support, Pre-Sales, Bug Reports).");
    }

    private static Queue BuildQueue(string slug, string name, string description, bool isDefault, int order, DateTimeOffset now) => new()
    {
        Slug = slug,
        Name = name,
        Description = description,
        IsDefault = isDefault,
        IsActive = true,
        DisplayOrder = order,
        CreatedById = Guid.Empty,
        CreatedOn = now
    };
}
