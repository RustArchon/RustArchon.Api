using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Turns TenantPlan from a one-row-per-tenant current-state table into subscription history:
    /// StartDate/EndDate intervals, with the open row (EndDate IS NULL) as the tenant's current plan.
    /// See <see cref="Data.TenantPlan"/>'s remarks for the model this establishes.
    /// </summary>
    /// <remarks>
    /// EF scaffolded this as DROP AssignedAtUtc + ADD StartDate, which would have discarded every
    /// existing tenant's real assignment date and replaced it with the moment the migration ran. It's
    /// hand-edited to a RENAME instead: AssignedAtUtc already held exactly what StartDate means, so
    /// existing history is preserved rather than flattened to "everyone started today".
    /// </remarks>
    public partial class TenantPlanHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Must go before the partial index is created - this is the constraint that limited a
            // tenant to one row ever, which is precisely what history needs to stop doing.
            migrationBuilder.DropIndex(
                name: "IX_TenantPlan_TenantId",
                table: "TenantPlan");

            // Rename, not drop-and-add: same column, same values, clearer name now that a row is an
            // interval rather than a single assignment.
            migrationBuilder.RenameColumn(
                name: "AssignedAtUtc",
                table: "TenantPlan",
                newName: "StartDate");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "StartDate",
                table: "TenantPlan",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            // Nullable, and null is the meaningful value: it marks the row as the current plan.
            // Every pre-existing row is a current plan, so they all correctly default to null here.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndDate",
                table: "TenantPlan",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlan_TenantId_StartDate",
                table: "TenantPlan",
                columns: new[] { "TenantId", "StartDate" });

            // The replacement invariant: at most one *open* row per tenant, any number of closed ones.
            // Satisfiable on existing data for free - the index dropped above already guaranteed one
            // row per tenant, and every one of those rows is open as of this migration.
            migrationBuilder.CreateIndex(
                name: "IX_TenantPlan_TenantId_WhereCurrent",
                table: "TenantPlan",
                column: "TenantId",
                unique: true,
                filter: "\"EndDate\" IS NULL");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Reverting is only clean while no plan change has actually been recorded. The old schema
        /// cannot represent more than one row per tenant, so if any tenant has closed intervals by the
        /// time this runs, recreating IX_TenantPlan_TenantId fails on a duplicate-key violation. That
        /// is deliberate: the alternative is silently deleting subscription history to make the old
        /// index fit, which is a far worse outcome than a loud failure. Resolve it by deciding
        /// explicitly what to do with the closed rows first.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantPlan_TenantId_StartDate",
                table: "TenantPlan");

            migrationBuilder.DropIndex(
                name: "IX_TenantPlan_TenantId_WhereCurrent",
                table: "TenantPlan");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "TenantPlan");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "StartDate",
                table: "TenantPlan",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now()");

            migrationBuilder.RenameColumn(
                name: "StartDate",
                table: "TenantPlan",
                newName: "AssignedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlan_TenantId",
                table: "TenantPlan",
                column: "TenantId",
                unique: true);
        }
    }
}
