using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanSupersededBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByPlanId",
                table: "Plan",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plan_SupersededByPlanId",
                table: "Plan",
                column: "SupersededByPlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_Plan_Plan_SupersededByPlanId",
                table: "Plan",
                column: "SupersededByPlanId",
                principalTable: "Plan",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // The plans that were replaced before this was recorded: rebuilt from the only trace there was (same name, created later). A reconstruction,
            // not a record - see PlanVersionBackfill for the rule and where it can be wrong.
            migrationBuilder.Sql(RustArchon.Api.Data.PlanVersionBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Plan_Plan_SupersededByPlanId",
                table: "Plan");

            migrationBuilder.DropIndex(
                name: "IX_Plan_SupersededByPlanId",
                table: "Plan");

            migrationBuilder.DropColumn(
                name: "SupersededByPlanId",
                table: "Plan");
        }
    }
}
