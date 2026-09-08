// Copyright ©2026 Scott Blomfield

using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Infrastructure.Security;

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
    : JumpStartDbContext(options, tenantContext), IDataProtectionKeyContext
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
    /// Gets or sets the Subscription DbSet.
    /// </summary>
    public DbSet<Subscription> Subscriptions { get; set; } = null!;

    /// <summary>
    /// Gets or sets the ConnectionLogEntry DbSet.
    /// </summary>
    public DbSet<ConnectionLogEntry> ConnectionLogEntries { get; set; } = null!;

    /// <summary>
    /// Gets or sets the ScheduledPlanChange DbSet.
    /// </summary>
    public DbSet<ScheduledPlanChange> ScheduledPlanChanges { get; set; } = null!;

    /// <summary>
    /// Gets or sets the SubscriptionPeriod DbSet - billing periods, one row per billable span.
    /// </summary>
    public DbSet<SubscriptionPeriod> SubscriptionPeriods { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PlanPrice DbSet - what each Plan costs, one row per term offered.
    /// </summary>
    public DbSet<PlanPrice> PlanPrices { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Invoice DbSet - documents asking a tenant for money.
    /// </summary>
    public DbSet<Invoice> Invoices { get; set; } = null!;

    /// <summary>
    /// Gets or sets the InvoiceLine DbSet - individual charges on those documents.
    /// </summary>
    public DbSet<InvoiceLine> InvoiceLines { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Payment DbSet - money arriving, independent of what it settles.
    /// </summary>
    public DbSet<Payment> Payments { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PaymentAllocation DbSet - how much of a payment settles which invoice.
    /// </summary>
    public DbSet<PaymentAllocation> PaymentAllocations { get; set; } = null!;

    /// <summary>
    /// Gets or sets the CreditNote DbSet - value granted back without money moving.
    /// </summary>
    public DbSet<CreditNote> CreditNotes { get; set; } = null!;

    /// <summary>
    /// Gets or sets the InvoiceNumberSequence DbSet.
    /// </summary>
    public DbSet<InvoiceNumberSequence> InvoiceNumberSequences { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DataProtectionKeys DbSet - the key ring, so RCON passwords stay decryptable
    /// across instances and container rebuilds.
    /// </summary>
    /// <remarks>
    /// <see cref="DataProtectionKey"/> is the framework's own entity, not one of ours.
    /// <c>IDataProtectionKeyContext</c> demands this exact type, and a structurally identical class
    /// of our own does not satisfy it - the interface is how <c>PersistKeysToDbContext</c> finds the
    /// set, and it matches by type rather than by shape.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

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

        // At most one *global* role per Name. JumpStart's own index is UNIQUE (TenantId, Name), and
        // Postgres treats NULLs as distinct in a unique index - so nothing stopped a second role
        // called "Owner" with TenantId NULL, which is precisely the shape the built-in roles use.
        // Two rows wearing one name, one of them granting who-knows-what, is the failure this
        // prevents. Filtered to the global rows so tenant-owned names stay governed by JumpStart's
        // index and two Organizations can both have a "Moderator". Same technique as Plan,
        // Subscription and ScheduledPlanChange below.
        modelBuilder.Entity<JumpStart.Authorization.Role>()
            .HasIndex(r => r.Name)
            .IsUnique()
            .HasFilter("\"TenantId\" IS NULL")
            .HasDatabaseName("IX_Role_Name_WhereGlobal");

        // Partial unique index: at most one Active Plan per Name. A plain (non-filtered) unique index
        // on Name alone would wrongly limit this table to one row per Name ever - see Plan's own
        // remarks for why many historical rows per Name is the whole point. Npgsql maps HasFilter to a
        // native Postgres partial index (CREATE UNIQUE INDEX ... WHERE "Active").
        modelBuilder.Entity<Plan>()
            .HasIndex(p => p.Name)
            .IsUnique()
            .HasFilter("\"Active\"")
            .HasDatabaseName("IX_Plan_Name_WhereActive");

        // Same technique as Plan's index directly above, for the same reason: Subscription is subscription
        // *history*, so a tenant legitimately has many rows and only the open one (EndDate IS NULL) is
        // its current plan. A plain unique index on TenantId would allow exactly one row per tenant ever
        // and make recording a plan change impossible - which is what this table used to be, before it
        // grew StartDate/EndDate. Filtering to the open row enforces the real invariant ("at most one
        // *current* plan per tenant") while leaving closed rows unconstrained.
        modelBuilder.Entity<Subscription>()
            .HasIndex(tp => tp.TenantId)
            .IsUnique()
            .HasFilter("\"EndDate\" IS NULL")
            .HasDatabaseName("IX_Subscription_TenantId_WhereCurrent");

        // The above index only covers the open row, so it can't serve a history query - this one does,
        // in the order GetHistoryForTenantAsync reads them.
        modelBuilder.Entity<Subscription>()
            .HasIndex(tp => new { tp.TenantId, tp.StartDate })
            .HasDatabaseName("IX_Subscription_TenantId_StartDate");

        // How the current slice is found (newest first) and how billing history is listed - the same
        // ordering serves both, since "current" is just the first row of the history.
        modelBuilder.Entity<SubscriptionPeriod>()
            .HasIndex(t => new { t.SubscriptionId, t.StartDate })
            .HasDatabaseName("IX_SubscriptionPeriod_SubscriptionId_StartDate");

        // A plan is offered on a given term at most once. Not a partial index this time - there's no
        // "current row" notion here, a plan simply either has a price for a term or doesn't, and a
        // superseded plan carries its own prices rather than sharing them.
        modelBuilder.Entity<PlanPrice>()
            .HasIndex(pp => new { pp.PlanId, pp.TermMonths })
            .IsUnique()
            .HasDatabaseName("IX_PlanPrice_PlanId_TermMonths");

        // "Defaulted to the record create date": a row inserted without an explicit StartDate is
        // stamped by Postgres instead of landing on DateTimeOffset.MinValue. EF omits the column from
        // the INSERT only when the property is still at its CLR default, so an explicitly-set StartDate
        // (what ChangePlanAsync does, to keep intervals contiguous) always wins over this.
        modelBuilder.Entity<Subscription>()
            .Property(tp => tp.StartDate)
            .HasDefaultValueSql("now()");

        // Third use of the same partial-index technique, for the same shape of invariant: a tenant may
        // have any number of ScheduledPlanChange rows over time (applied ones and cancelled ones are
        // kept as a record of what happened), but only ever one still waiting to happen. Requesting a
        // new change cancels the queued one rather than stacking a second, so this index is what makes
        // "which queued change wins?" an unaskable question.
        modelBuilder.Entity<ScheduledPlanChange>()
            .HasIndex(spc => spc.TenantId)
            .IsUnique()
            .HasFilter("\"AppliedOn\" IS NULL AND \"CancelledOn\" IS NULL")
            .HasDatabaseName("IX_ScheduledPlanChange_TenantId_WherePending");

        // How SubscriptionScheduleService finds work: everything due, oldest first, across all tenants.
        modelBuilder.Entity<ScheduledPlanChange>()
            .HasIndex(spc => spc.EffectiveDate)
            .HasFilter("\"AppliedOn\" IS NULL AND \"CancelledOn\" IS NULL")
            .HasDatabaseName("IX_ScheduledPlanChange_EffectiveDate_WherePending");

        // Fourth use of the partial-index technique, and the one that carries a legal requirement rather
        // than a modelling one: an invoice number must be unique, but a draft hasn't got one yet and
        // several drafts can be in flight at once. Filtering to non-null is what lets NULL mean "not
        // finalised" without every draft colliding with every other draft.
        modelBuilder.Entity<Invoice>()
            .HasIndex(i => i.Number)
            .IsUnique()
            .HasFilter("\"Number\" IS NOT NULL")
            .HasDatabaseName("IX_Invoice_Number_WhereNumbered");

        // The receivables query: everything a tenant owes, oldest due first. Filtered to Open because
        // that is the only status that can be outstanding - a paid, voided or written-off invoice is
        // never chased, so keeping them out of the index keeps it small as history accumulates.
        modelBuilder.Entity<Invoice>()
            .HasIndex(i => new { i.TenantId, i.DueOn })
            .HasFilter("\"Status\" = 1")
            .HasDatabaseName("IX_Invoice_TenantId_DueOn_WhereOpen");

        // Aging across all tenants - what the delinquency report reads, ordered the way it reads it.
        modelBuilder.Entity<Invoice>()
            .HasIndex(i => i.DueOn)
            .HasFilter("\"Status\" = 1")
            .HasDatabaseName("IX_Invoice_DueOn_WhereOpen");

        modelBuilder.Entity<InvoiceLine>()
            .HasIndex(l => l.InvoiceId)
            .HasDatabaseName("IX_InvoiceLine_InvoiceId");

        // Nullable, so this is the index behind "has this period been billed yet?" - which is exactly
        // the question invoice generation asks to avoid billing a period twice.
        modelBuilder.Entity<InvoiceLine>()
            .HasIndex(l => l.SubscriptionPeriodId)
            .HasDatabaseName("IX_InvoiceLine_SubscriptionPeriodId");

        modelBuilder.Entity<Payment>()
            .HasIndex(p => new { p.TenantId, p.ReceivedOn })
            .HasDatabaseName("IX_Payment_TenantId_ReceivedOn");

        // Both directions get read: an invoice needs its settlements, and reversing a payment needs
        // everything it settled.
        modelBuilder.Entity<PaymentAllocation>()
            .HasIndex(a => a.InvoiceId)
            .HasDatabaseName("IX_PaymentAllocation_InvoiceId");

        modelBuilder.Entity<PaymentAllocation>()
            .HasIndex(a => a.PaymentId)
            .HasDatabaseName("IX_PaymentAllocation_PaymentId");

        modelBuilder.Entity<CreditNote>()
            .HasIndex(c => c.TenantId)
            .HasDatabaseName("IX_CreditNote_TenantId");

        // Deleting an invoice must not silently take its settlement records with it - and in practice
        // nothing deletes an invoice at all, since a finalised one is corrected by credit note rather
        // than removed. Restrict makes an accidental delete fail loudly instead of cascading through
        // the payment history.
        modelBuilder.Entity<PaymentAllocation>()
            .HasOne(a => a.Invoice)
            .WithMany()
            .HasForeignKey(a => a.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Same reasoning for the line-to-period link, pointed the other way: a period is subscription
        // history and is never deleted, but if one ever were, taking an issued invoice's line with it
        // would alter a finalised document.
        modelBuilder.Entity<InvoiceLine>()
            .HasOne(l => l.SubscriptionPeriod)
            .WithMany()
            .HasForeignKey(l => l.SubscriptionPeriodId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InvoiceNumberSequence>()
            .HasIndex(s => s.Scope)
            .IsUnique()
            .HasDatabaseName("IX_InvoiceNumberSequence_Scope");
    }
}
