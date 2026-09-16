using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentDisputeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisputeDueBy",
                table: "Payment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisputeEvidenceSubmittedOn",
                table: "Payment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeId",
                table: "Payment",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeReason",
                table: "Payment",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisputeDueBy",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "DisputeEvidenceSubmittedOn",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "DisputeId",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "DisputeReason",
                table: "Payment");
        }
    }
}
