using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Adds purchased capacity - see <c>TenantPlanTerm.Quantity</c> - as the third dimension of a
    /// subscription alongside plan and term.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated version of this defaulted both columns to <c>0</c>, which would have been a live
    /// outage rather than a cosmetic wrong default: server creation is now gated on unused slots, so
    /// every existing subscriber would have been left holding zero and unable to add anything, and the
    /// renewal price of a per-unit plan would have been computed against zero units. Both columns
    /// default to <c>1</c> instead, and the existing rows are backfilled from the catalog below.
    /// </para>
    /// <para>
    /// The backfill lands each row at the greater of what its plan already includes and what the tenant
    /// is actually running, so no existing subscriber loses a server to this migration and nobody is
    /// silently granted capacity they didn't buy.
    /// </para>
    /// </remarks>
    public partial class Entitlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "TenantPlanTerm",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "ScheduledPlanChange",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Every existing billing period: the plan's included units for that period's term, floored
            // at the tenant's live server count. Soft-deleted servers are excluded, matching what the
            // application counts. COALESCE covers a period whose plan carries no price row for its term,
            // which is a catalog defect the reports surface rather than something to fail a migration on.
            migrationBuilder.Sql("""
                UPDATE "TenantPlanTerm" AS t
                SET "Quantity" = GREATEST(
                    COALESCE((
                        SELECT pp."IncludedUnits"
                        FROM "PlanPrice" pp
                        JOIN "TenantPlan" tp ON tp."Id" = t."TenantPlanId"
                        WHERE pp."PlanId" = tp."PlanId" AND pp."TermMonths" = t."TermMonths"
                    ), 1),
                    COALESCE((
                        SELECT COUNT(*)::int
                        FROM "RustServer" s
                        JOIN "TenantPlan" tp2 ON tp2."Id" = t."TenantPlanId"
                        WHERE s."TenantId" = tp2."TenantId" AND s."DeletedOn" IS NULL
                    ), 0),
                    1);
                """);

            // Pending changes take the target plan's included units. Deliberately not floored at the
            // server count: the scheduler re-checks that rule at the moment it applies, and a change
            // quietly rewritten here to something the tenant never accepted would be worse than one that
            // stays pending until it's valid.
            migrationBuilder.Sql("""
                UPDATE "ScheduledPlanChange" AS c
                SET "Quantity" = GREATEST(
                    COALESCE((
                        SELECT pp."IncludedUnits"
                        FROM "PlanPrice" pp
                        WHERE pp."PlanId" = c."PlanId" AND pp."TermMonths" = c."TermMonths"
                    ), 1),
                    1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "TenantPlanTerm");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "ScheduledPlanChange");
        }
    }
}
