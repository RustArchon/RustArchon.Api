using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRustServerLifecycleAndStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedWorkerId",
                table: "RustServer",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConnectionStatus",
                table: "RustServer",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConnectionStatusChangedAtUtc",
                table: "RustServer",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectionStatusDetail",
                table: "RustServer",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // defaultValue: true, not the tool-generated false - existing rows (servers registered
            // before this column existed) should come back as enabled, matching RustServer.IsEnabled's
            // own C# property initializer. EF's scaffolding defaults a new non-nullable bool column to
            // false regardless of the entity's initializer; it has to be corrected by hand.
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "RustServer",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHeartbeatUtc",
                table: "RustServer",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedWorkerId",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "ConnectionStatus",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "ConnectionStatusChangedAtUtc",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "ConnectionStatusDetail",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatUtc",
                table: "RustServer");
        }
    }
}
