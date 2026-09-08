using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Moves plan pricing out of three fixed columns and into <see cref="Data.PlanPrice"/> rows, one per
    /// term a plan is actually offered on, and lets a plan charge per server instead of capping them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two limitations of the old shape drove this. Three non-nullable price columns forced every plan to
    /// be sold on all three terms - "not available annually" was inexpressible, because a price of zero
    /// already means free. And a single <c>MaximumServers</c> ceiling could only cap capacity, never
    /// charge for it.
    /// </para>
    /// <para>
    /// EF scaffolded the three <c>DropColumn</c> calls <em>first</em>, which would have destroyed every
    /// existing price before there was anywhere to put it. They're reordered below to run after the new
    /// table exists and has been backfilled from them.
    /// </para>
    /// <para>
    /// The two <c>Term</c> columns become <c>TermMonths</c> by rename rather than drop-and-add: the old
    /// enum stored month counts as its values (Monthly = 1, Quarterly = 3, Annual = 12), so every
    /// existing row's number is already correct as a plain integer and carries over untouched.
    /// </para>
    /// </remarks>
    public partial class PlanPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanPrice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    TermMonths = table.Column<int>(type: "integer", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    IncludedUnits = table.Column<int>(type: "integer", nullable: false),
                    UnitAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanPrice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanPrice_Plan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanPrice_PlanId_TermMonths",
                table: "PlanPrice",
                columns: new[] { "PlanId", "TermMonths" },
                unique: true);

            // Backfill, before the source columns go away. Every existing plan keeps all three terms at
            // exactly the prices it already had, so nothing a subscriber sees changes.
            //
            // IncludedUnits is seeded from the plan's own ceiling and UnitAmount left at zero - that's
            // precisely the flat-tier case of the pricing formula, so these plans keep behaving as caps
            // rather than silently becoming per-server. A plan with no ceiling can't arise here; the
            // column is still NOT NULL at this point.
            migrationBuilder.Sql("""
                INSERT INTO "PlanPrice"
                    ("Id", "PlanId", "TermMonths", "BaseAmount", "IncludedUnits", "UnitAmount", "Currency")
                SELECT gen_random_uuid(),
                       p."Id",
                       t.months,
                       CASE t.months
                           WHEN 1  THEN p."MonthlyPrice"
                           WHEN 3  THEN p."QuarterlyPrice"
                           ELSE         p."AnnualPrice"
                       END,
                       COALESCE(p."MaximumServers", 0),
                       0,
                       'USD'
                FROM "Plan" p
                CROSS JOIN (VALUES (1), (3), (12)) AS t(months);
                """);

            migrationBuilder.DropColumn(name: "MonthlyPrice", table: "Plan");
            migrationBuilder.DropColumn(name: "QuarterlyPrice", table: "Plan");
            migrationBuilder.DropColumn(name: "AnnualPrice", table: "Plan");

            // Null now means "no ceiling" rather than "zero servers allowed" - see Plan.MaximumServers.
            migrationBuilder.AlterColumn<int>(
                name: "MaximumServers",
                table: "Plan",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // 0 = Flat, which is what every existing plan is.
            migrationBuilder.AddColumn<int>(
                name: "PricingModel",
                table: "Plan",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.RenameColumn(
                name: "Term",
                table: "TenantPlanTerm",
                newName: "TermMonths");

            migrationBuilder.RenameColumn(
                name: "Term",
                table: "ScheduledPlanChange",
                newName: "TermMonths");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Restores the three columns from each plan's monthly, quarterly and annual rows. A plan sold on
        /// some other term, or on fewer than all three, cannot be represented by the old shape - those
        /// terms are simply lost, and a plan missing one of the three gets a zero. Reverting is only
        /// clean while the catalog still looks the way this migration found it.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyPrice", table: "Plan", type: "numeric(18,2)", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>(
                name: "QuarterlyPrice", table: "Plan", type: "numeric(18,2)", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>(
                name: "AnnualPrice", table: "Plan", type: "numeric(18,2)", nullable: false, defaultValue: 0m);

            migrationBuilder.Sql("""
                UPDATE "Plan" p SET
                    "MonthlyPrice"   = COALESCE((SELECT pp."BaseAmount" FROM "PlanPrice" pp WHERE pp."PlanId" = p."Id" AND pp."TermMonths" = 1), 0),
                    "QuarterlyPrice" = COALESCE((SELECT pp."BaseAmount" FROM "PlanPrice" pp WHERE pp."PlanId" = p."Id" AND pp."TermMonths" = 3), 0),
                    "AnnualPrice"    = COALESCE((SELECT pp."BaseAmount" FROM "PlanPrice" pp WHERE pp."PlanId" = p."Id" AND pp."TermMonths" = 12), 0);
                """);

            // A plan with no ceiling has no honest representation in the old shape either; zero is the
            // only value the non-null column can take, and it means something quite different.
            migrationBuilder.Sql(@"UPDATE ""Plan"" SET ""MaximumServers"" = 0 WHERE ""MaximumServers"" IS NULL;");

            migrationBuilder.AlterColumn<int>(
                name: "MaximumServers",
                table: "Plan",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.DropTable(name: "PlanPrice");
            migrationBuilder.DropColumn(name: "PricingModel", table: "Plan");

            migrationBuilder.RenameColumn(
                name: "TermMonths",
                table: "TenantPlanTerm",
                newName: "Term");

            migrationBuilder.RenameColumn(
                name: "TermMonths",
                table: "ScheduledPlanChange",
                newName: "Term");
        }
    }
}
