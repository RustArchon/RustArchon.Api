using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddServerSetupCompleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SetupCompletedAtUtc",
                table: "RustServer",
                type: "timestamp with time zone",
                nullable: true);

            // Every server that exists now was added before this flag did: none of them is half-added, so all count as complete from when they were created.
            migrationBuilder.Sql("UPDATE \"RustServer\" SET \"SetupCompletedAtUtc\" = \"CreatedOn\" WHERE \"SetupCompletedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SetupCompletedAtUtc",
                table: "RustServer");
        }
    }
}
