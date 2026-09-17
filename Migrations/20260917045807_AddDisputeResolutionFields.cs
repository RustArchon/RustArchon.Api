using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDisputeResolutionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DisputeAmountReversed",
                table: "Payment",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisputeClosedOn",
                table: "Payment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisputeFundsReinstatedOn",
                table: "Payment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeStatus",
                table: "Payment",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisputeAmountReversed",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "DisputeClosedOn",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "DisputeFundsReinstatedOn",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "DisputeStatus",
                table: "Payment");
        }
    }
}
