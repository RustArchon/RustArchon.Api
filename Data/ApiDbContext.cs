// Copyright ©2026 Scott Blomfield

using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// Database context for the RustArchon API, containing <see cref="RustServer"/> and every entity
/// JumpStart itself contributes (Tenant, Role, UserPermission, etc.).
/// </summary>
/// <remarks>
/// Inherits from <see cref="JumpStartDbContext"/> so framework-required data is seeded automatically,
/// and forwards the optional <see cref="ITenantContext"/> to the base class to enable multi-tenant
/// data isolation - registered as <c>JwtTenantContext</c> in <c>Program.cs</c>.
/// </remarks>
public class ApiDbContext(DbContextOptions<ApiDbContext> options, ITenantContext? tenantContext = null)
    : JumpStartDbContext(options, tenantContext)
{
    /// <summary>
    /// Gets or sets the RustServer DbSet.
    /// </summary>
    public DbSet<RustServer> RustServers { get; set; } = null!;

    /// <summary>
    /// Gets or sets the InvitationCode DbSet.
    /// </summary>
    public DbSet<InvitationCode> InvitationCodes { get; set; } = null!;

    /// <summary>
    /// Gets or sets the RconEvent DbSet.
    /// </summary>
    public DbSet<RconEvent> RconEvents { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PlatformSetting DbSet.
    /// </summary>
    public DbSet<PlatformSetting> PlatformSettings { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PlayerSession DbSet.
    /// </summary>
    public DbSet<PlayerSession> PlayerSessions { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PlayerKillEvent DbSet.
    /// </summary>
    public DbSet<PlayerKillEvent> PlayerKillEvents { get; set; } = null!;

    /// <summary>
    /// Gets or sets the ServerInfoSnapshot DbSet.
    /// </summary>
    public DbSet<ServerInfoSnapshot> ServerInfoSnapshots { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Plan DbSet.
    /// </summary>
    public DbSet<Plan> Plans { get; set; } = null!;

    /// <summary>
    /// Gets or sets the TenantPlan DbSet.
    /// </summary>
    public DbSet<TenantPlan> TenantPlans { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Call base first - applies framework configurations and seeds framework data.
        base.OnModelCreating(modelBuilder);

        // JumpStart's Tenant.Settings and UserTenant.Settings are annotated with the SQL-Server-only
        // [Column(TypeName = "nvarchar(max)")] - fine on SQL Server, invalid DDL on PostgreSQL. This
        // is a Fluent API override of a base class's DataAnnotation, not a choice of Fluent API over
        // DataAnnotations for new code - the annotation lives in JumpStart, which this project
        // doesn't own and shouldn't fork just to fix one provider-specific type name. Both properties
        // fall back to Npgsql's own default "unbounded text" mapping (`text`) once the explicit type
        // name is cleared.
        modelBuilder.Entity<Tenant>().Property(t => t.Settings).HasColumnType(null);
        modelBuilder.Entity<UserTenant>().Property(t => t.Settings).HasColumnType(null);

        // Partial unique index: at most one Active Plan per Name. A plain (non-filtered) unique index
        // on Name alone would wrongly limit this table to one row per Name ever - see Plan's own
        // remarks for why many historical rows per Name is the whole point. Npgsql maps HasFilter to a
        // native Postgres partial index (CREATE UNIQUE INDEX ... WHERE "Active").
        modelBuilder.Entity<Plan>()
            .HasIndex(p => p.Name)
            .IsUnique()
            .HasFilter("\"Active\"")
            .HasDatabaseName("IX_Plan_Name_WhereActive");
    }
}
