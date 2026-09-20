using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginAutoUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PluginAutoUpdateEnabled",
                table: "RustServer",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PluginUpdateAttempt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ToVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginUpdateAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginUpdateAttempt_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginUpdateAttempt_Server_StartedAtUtc",
                table: "PluginUpdateAttempt",
                columns: new[] { "RustServerId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginUpdateAttempt_Tenant_StartedAtUtc",
                table: "PluginUpdateAttempt",
                columns: new[] { "TenantId", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginUpdateAttempt");

            migrationBuilder.DropColumn(
                name: "PluginAutoUpdateEnabled",
                table: "RustServer");
        }
    }
}
