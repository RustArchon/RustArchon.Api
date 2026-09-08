using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Adds billing periods (<see cref="Data.TenantPlanTerm"/>) alongside the existing plan history,
    /// and gives every current subscription its first one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Data.TenantPlan"/> is untouched: it keeps meaning "which plan, since when", and every
    /// billing period a tenant is charged for becomes a row in the new table instead. Renewal inserts
    /// there rather than overwriting anything, so the billing history survives - an earlier attempt put
    /// the period on <c>TenantPlan</c> itself and had renewal advance it in place, which left a tenant
    /// two years into an unchanged plan with one row and no record of the periods it had rolled past.
    /// That version was never applied anywhere and has been folded into this one rather than shipped
    /// and then undone.
    /// </para>
    /// <para>
    /// EF also scaffolded four <c>DropColumn</c> calls here against period columns from that abandoned
    /// attempt. They were removed by hand: those columns exist in no database this migration will ever
    /// run against, so dropping them would fail on the first real deployment.
    /// </para>
    /// </remarks>
    public partial class Subscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The queue of plan/term changes accepted but not yet in force - every downgrade and every
            // term reduction lands here first, since neither takes effect until the paid-for period ends.
            migrationBuilder.CreateTable(
                name: "ScheduledPlanChange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Term = table.Column<int>(type: "integer", nullable: false),
                    EffectiveDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppliedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledPlanChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledPlanChange_Plan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduledPlanChange_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledPlanChange_PlanId",
                table: "ScheduledPlanChange",
                column: "PlanId");

            // At most one change queued per tenant - a new request supersedes whatever was pending
            // rather than stacking, so "which queued change wins?" is never a question.
            migrationBuilder.CreateIndex(
                name: "IX_ScheduledPlanChange_TenantId_WherePending",
                table: "ScheduledPlanChange",
                column: "TenantId",
                unique: true,
                filter: "\"AppliedOn\" IS NULL AND \"CancelledOn\" IS NULL");

            // How the background applier finds work: everything due, oldest first, across all tenants.
            migrationBuilder.CreateIndex(
                name: "IX_ScheduledPlanChange_EffectiveDate_WherePending",
                table: "ScheduledPlanChange",
                column: "EffectiveDate",
                filter: "\"AppliedOn\" IS NULL AND \"CancelledOn\" IS NULL");

            migrationBuilder.CreateTable(
                name: "TenantPlanTerm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Term = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    BilledOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPlanTerm", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantPlanTerm_TenantPlan_TenantPlanId",
                        column: x => x.TenantPlanId,
                        principalTable: "TenantPlan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlanTerm_TenantPlanId_StartDate",
                table: "TenantPlanTerm",
                columns: new[] { "TenantPlanId", "StartDate" });

            // Give every *current* subscription its opening billing period: monthly, anchored at the
            // date the tenant went onto the plan, priced at that plan's monthly rate. Monthly because
            // nothing has ever recorded a term and it's the shortest commitment - it never over-claims
            // what somebody has paid for.
            //
            // Closed TenantPlan rows deliberately get nothing. They're plans the tenant has already left
            // and no billing was ever recorded against them; inventing periods to fill the gap would put
            // fabricated amounts into a billing history that is supposed to be a record of fact.
            //
            // A period computed this way is usually already in the past, which is correct rather than a
            // problem: SubscriptionScheduleService rolls each subscription forward on its first pass,
            // inserting a real row per period as it goes, so genuine history starts accumulating from
            // deployment rather than being back-dated.
            migrationBuilder.Sql("""
                INSERT INTO "TenantPlanTerm"
                    ("Id", "TenantPlanId", "Term", "PeriodStart", "PeriodEnd", "StartDate", "EndDate", "Amount")
                SELECT gen_random_uuid(),
                       tp."Id",
                       1,
                       tp."StartDate",
                       tp."StartDate" + interval '1 month',
                       tp."StartDate",
                       tp."StartDate" + interval '1 month',
                       p."MonthlyPrice"
                FROM "TenantPlan" tp
                JOIN "Plan" p ON p."Id" = tp."PlanId"
                WHERE tp."EndDate" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantPlanTerm");

            migrationBuilder.DropTable(
                name: "ScheduledPlanChange");
        }
    }
}
