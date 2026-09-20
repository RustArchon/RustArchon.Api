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
    /// Gets or sets the TenantBillingAddress DbSet. Needs an explicit property, unlike most of the
    /// billing subsystem's own entities (<see cref="Invoice"/>, <see cref="InvoiceLine"/>, ...), which
    /// EF Core discovers by walking navigation properties from an already-registered DbSet - nothing
    /// navigates <em>to</em> this one, so without this property it's invisible to the model entirely.
    /// </summary>
    public DbSet<TenantBillingAddress> TenantBillingAddresses { get; set; } = null!;

    /// <summary>
    /// Gets or sets the BlockedInvoiceIssuance DbSet. Same reason as <see cref="TenantBillingAddresses"/>
    /// just above - nothing navigates to <see cref="BlockedInvoiceIssuance"/> either, so it needs the
    /// same explicit property to be discoverable at all.
    /// </summary>
    public DbSet<BlockedInvoiceIssuance> BlockedInvoiceIssuances { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Discount and DiscountRedemption DbSets. Same reason as
    /// <see cref="BlockedInvoiceIssuances"/> just above - nothing navigates to either of these from an
    /// already-registered DbSet, so both need an explicit property to be discoverable at all.
    /// </summary>
    public DbSet<Discount> Discounts { get; set; } = null!;

    public DbSet<DiscountRedemption> DiscountRedemptions { get; set; } = null!;

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
    /// Gets or sets the ServerReport DbSet.
    /// </summary>
    public DbSet<ServerReport> ServerReports { get; set; } = null!;

    /// <summary>
    /// Gets or sets the ServerInfoSnapshot DbSet.
    /// </summary>
    public DbSet<ServerInfoSnapshot> ServerInfoSnapshots { get; set; } = null!;

    /// <summary>
    /// Gets or sets the ServerPlugin DbSet.
    /// </summary>
    public DbSet<ServerPlugin> ServerPlugins { get; set; } = null!;

    /// <summary>
    /// Gets or sets the ServerPluginStatus DbSet.
    /// </summary>
    public DbSet<ServerPluginStatus> ServerPluginStatuses { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginUpdateToken DbSet.
    /// </summary>
    public DbSet<PluginUpdateToken> PluginUpdateTokens { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginKeyHistory DbSet - retired and revoked plugin signing keys.
    /// </summary>
    public DbSet<PluginKeyHistory> PluginKeyHistories { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginAdminEvent DbSet - the audit log of plugin key and release actions.
    /// </summary>
    public DbSet<PluginAdminEvent> PluginAdminEvents { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginRelease DbSet - plugin source files uploaded for delivery.
    /// </summary>
    public DbSet<PluginRelease> PluginReleases { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginCombatChunk DbSet - batches of combat events drained from the RustArchon plugin.
    /// </summary>
    public DbSet<PluginCombatChunk> PluginCombatChunks { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginTcSnapshot DbSet - each server's current tool cupboard list from the RustArchon plugin.
    /// </summary>
    public DbSet<PluginTcSnapshot> PluginTcSnapshots { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginPositionChunk DbSet - batches of player position samples drained from the RustArchon plugin.
    /// </summary>
    public DbSet<PluginPositionChunk> PluginPositionChunks { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginMap DbSet - each server's world map (per wipe) as reported by the RustArchon plugin.
    /// </summary>
    public DbSet<PluginMap> PluginMaps { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginMapUploadToken DbSet - single-use permissions for a game server to send a map picture.
    /// </summary>
    public DbSet<PluginMapUploadToken> PluginMapUploadTokens { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginUpdateNotice DbSet - the newest update UpdateChecker has reported for each plugin on each server.
    /// </summary>
    public DbSet<PluginUpdateNotice> PluginUpdateNotices { get; set; } = null!;

    /// <summary>
    /// Gets or sets the PluginUpdateAttempt DbSet - each time the Panel asked a server to update its plugin or Updater, and how it went.
    /// </summary>
    public DbSet<PluginUpdateAttempt> PluginUpdateAttempts { get; set; } = null!;

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

    /// <summary>
    /// Gets or sets the Notes DbSet - a site admin's own annotations on Organizations and people.
    /// </summary>
    public DbSet<Note> Notes { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Communications DbSet - a permanent record of every outbound email and its
    /// delivery status. See <see cref="Communication"/>.
    /// </summary>
    public DbSet<Communication> Communications { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Queues DbSet - the buckets a <see cref="Ticket"/> is routed into. See
    /// <see cref="Queue"/>.
    /// </summary>
    public DbSet<Queue> Queues { get; set; } = null!;

    /// <summary>
    /// Gets or sets the TicketStatuses DbSet - the states a <see cref="Ticket"/> can be in. See
    /// <see cref="Data.TicketStatus"/>.
    /// </summary>
    public DbSet<TicketStatus> TicketStatuses { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Tickets DbSet. See <see cref="Ticket"/>.
    /// </summary>
    public DbSet<Ticket> Tickets { get; set; } = null!;

    /// <summary>
    /// Gets or sets the TicketMessages DbSet - a ticket's customer-visible thread. See
    /// <see cref="TicketMessage"/>.
    /// </summary>
    public DbSet<TicketMessage> TicketMessages { get; set; } = null!;

    /// <summary>
    /// Gets or sets the TicketNotes DbSet - staff-only annotations on a ticket. See
    /// <see cref="TicketNote"/>.
    /// </summary>
    public DbSet<TicketNote> TicketNotes { get; set; } = null!;

    /// <summary>
    /// Gets or sets the Themes DbSet - the catalog of uploaded theme packages. See <see cref="Theme"/>.
    /// </summary>
    public DbSet<Theme> Themes { get; set; } = null!;

    /// <summary>
    /// Gets or sets the EmailTemplates DbSet - the admin-editable Subject/HtmlBody sent for each kind
    /// of email RustArchon sends. See <see cref="EmailTemplate"/>.
    /// </summary>
    public DbSet<EmailTemplate> EmailTemplates { get; set; } = null!;

    /// <summary>
    /// Gets or sets the EmailPlaceholders DbSet - reusable <c>{{Token}}</c> placeholders shared across
    /// email templates. See <see cref="EmailPlaceholder"/>.
    /// </summary>
    public DbSet<EmailPlaceholder> EmailPlaceholders { get; set; } = null!;

    /// <summary>
    /// Gets or sets the EmailTemplateTranslations DbSet - the per-culture Subject/HtmlBody rows for
    /// each <see cref="EmailTemplate"/>. See <see cref="EmailTemplateTranslation"/>.
    /// </summary>
    public DbSet<EmailTemplateTranslation> EmailTemplateTranslations { get; set; } = null!;

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

        // Same technique again, for the same underlying reason as Role's index above: RustServer is
        // soft-deletable, so a plain unfiltered unique (TenantId, Name) index blocks ever reusing a
        // name that only a *deleted* row still holds - Postgres enforces the index against every
        // physical row, unaware of JumpStart's DeletedOn-based query filter, which only ever affects
        // reads. Confirmed live: delete a server, try to re-add one under the same name, and it fails
        // with a raw 23505 unique-violation reaching the caller as an unhandled 500, with no indication
        // anywhere that the "conflicting" server is the one that was just deleted. Filtering to
        // DeletedOn IS NULL enforces the real invariant ("at most one *live* server per name") while
        // leaving soft-deleted rows unconstrained, exactly like Role/Plan/Subscription above.
        modelBuilder.Entity<RustServer>()
            .HasIndex(s => new { s.TenantId, s.Name })
            .IsUnique()
            .HasFilter("\"DeletedOn\" IS NULL")
            .HasDatabaseName("IX_RustServer_TenantId_Name");

        // Both RustArchon-plugin switches are ON by default (opt-out). A C# property initializer alone is not
        // enough: EF's generated AddColumn for a non-nullable bool uses the type default (false), which would
        // silently switch every EXISTING server off. HasDefaultValue(true) makes the column default, and so
        // the migration's backfill, true. Verified in the AddPluginStatusAndSettings migration.
        modelBuilder.Entity<RustServer>()
            .Property(s => s.PluginRecordingEnabled)
            .HasDefaultValue(true);
        modelBuilder.Entity<RustServer>()
            .Property(s => s.PluginCombatLogEnabled)
            .HasDefaultValue(true);

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

        // How the Organizations and Users admin screens list notes: everything about this tenant, or
        // everything about this person. Both nullable, so neither index alone would serve a query for
        // "notes with no tenant" or "notes with no user" efficiently - not a real gap today, since
        // NotesController.List refuses a request naming neither.
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.TenantId)
            .HasDatabaseName("IX_Note_TenantId");

        modelBuilder.Entity<Note>()
            .HasIndex(n => n.UserId)
            .HasDatabaseName("IX_Note_UserId");

        // Same reasoning as the Note indexes above - the Organizations and Users admin screens each
        // list communications for one tenant or one user.
        modelBuilder.Entity<Communication>()
            .HasIndex(c => c.TenantId)
            .HasDatabaseName("IX_Communication_TenantId");

        modelBuilder.Entity<Communication>()
            .HasIndex(c => c.UserId)
            .HasDatabaseName("IX_Communication_UserId");

        modelBuilder.Entity<Queue>()
            .HasIndex(q => q.Slug)
            .IsUnique()
            .HasDatabaseName("IX_Queue_Slug");

        modelBuilder.Entity<TicketStatus>()
            .HasIndex(s => s.Slug)
            .IsUnique()
            .HasDatabaseName("IX_TicketStatus_Slug");

        // The staff console filters by queue, by status, and by tenant (an Organization's own ticket
        // history); GuestAccessToken is looked up on its own by the anonymous guest-ticket page, so it
        // gets a unique index rather than sharing one of these.
        modelBuilder.Entity<Ticket>()
            .HasIndex(t => t.QueueId)
            .HasDatabaseName("IX_Ticket_QueueId");

        modelBuilder.Entity<Ticket>()
            .HasIndex(t => t.StatusId)
            .HasDatabaseName("IX_Ticket_StatusId");

        modelBuilder.Entity<Ticket>()
            .HasIndex(t => t.TenantId)
            .HasDatabaseName("IX_Ticket_TenantId");

        modelBuilder.Entity<Ticket>()
            .HasIndex(t => t.GuestAccessToken)
            .IsUnique()
            .HasDatabaseName("IX_Ticket_GuestAccessToken");

        // A ticket is never deleted once submitted, so its Queue can't be either while any Ticket
        // still points at it - QueueSeeder/the admin UI retire a queue via IsActive instead.
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.Queue)
            .WithMany()
            .HasForeignKey(t => t.QueueId)
            .OnDelete(DeleteBehavior.Restrict);

        // Same reasoning as the Queue relationship just above - AdminTicketStatusesController checks
        // for in-use statuses itself before deleting one, but this is the backstop.
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.Status)
            .WithMany()
            .HasForeignKey(t => t.StatusId)
            .OnDelete(DeleteBehavior.Restrict);

        // How the staff console and the tenant/guest thread views both list a ticket's conversation.
        modelBuilder.Entity<TicketMessage>()
            .HasIndex(m => m.TicketId)
            .HasDatabaseName("IX_TicketMessage_TicketId");

        modelBuilder.Entity<TicketNote>()
            .HasIndex(n => n.TicketId)
            .HasDatabaseName("IX_TicketNote_TicketId");

        // Belt-and-suspenders alongside ThemeService.ActivateAsync's own application-level enforcement
        // (see Theme.IsActive's remarks) - a partial unique index means "more than one active theme"
        // can never happen even if some future code path forgets to clear the others first.
        modelBuilder.Entity<Theme>()
            .HasIndex(t => t.IsActive)
            .HasDatabaseName("IX_Theme_IsActive_Unique")
            .IsUnique()
            .HasFilter("\"IsActive\" = true");

        // Plain EF Core skip-navigation many-to-many - see EmailPlaceholder's own remarks for why this
        // isn't a modeled join entity the way RolePermission/UserRole are. Named explicitly rather than
        // left to EF's own generated default, so the table in Postgres reads the same way every other
        // join table in this schema does.
        modelBuilder.Entity<EmailTemplate>()
            .HasMany(t => t.Placeholders)
            .WithMany(p => p.Templates)
            .UsingEntity(j => j.ToTable("EmailTemplatePlaceholder"));

        // A translation is owned by exactly one template and never outlives it - deleting a template
        // (never done through the admin UI today, see EmailTemplatesController's remarks, but not
        // something the schema itself should forbid) should take its translations with it rather than
        // leaving orphaned rows behind.
        modelBuilder.Entity<EmailTemplateTranslation>()
            .HasOne(t => t.EmailTemplate)
            .WithMany(t => t.Translations)
            .HasForeignKey(t => t.EmailTemplateId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
