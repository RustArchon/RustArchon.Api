using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class PlanOnePerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OnePerOwner",
                table: "Plan",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Marks every plan that currently costs nothing on every term it is sold on. PlanSeeder
            // sets this on Wood, but it only runs against an empty catalogue - so without this, a
            // deployment that already has plans gets the column and none of the intent, and its free
            // tier stays an unlimited supply of free servers until somebody notices the new checkbox.
            //
            // Deliberately a one-time backfill of the default rather than an ongoing rule: the flag
            // is a decision a site admin owns, and "free" is only the reason it starts on, not what
            // it means. Unticking it afterwards is a supported answer and nothing re-applies this.
            migrationBuilder.Sql(
                """
                UPDATE "Plan" SET "OnePerOwner" = true
                WHERE "Id" IN (
                    SELECT p."Id" FROM "Plan" p
                    JOIN "PlanPrice" pp ON pp."PlanId" = p."Id"
                    GROUP BY p."Id"
                    HAVING SUM(pp."BaseAmount") = 0 AND SUM(pp."UnitAmount") = 0
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnePerOwner",
                table: "Plan");
        }
    }
}
