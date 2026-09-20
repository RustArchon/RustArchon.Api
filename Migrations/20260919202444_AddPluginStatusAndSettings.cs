using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginStatusAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PluginCombatLogEnabled",
                table: "RustServer",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PluginRecordingEnabled",
                table: "RustServer",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ServerPluginStatus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    PluginVersion = table.Column<string>(type: "text", nullable: false),
                    Capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                    ReportedRecordingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ReportedCombatLogEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SettingsPersisted = table.Column<bool>(type: "boolean", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerPluginStatus", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerPluginStatus_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerPluginStatus_TenantId_RustServerId",
                table: "ServerPluginStatus",
                columns: new[] { "TenantId", "RustServerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerPluginStatus");

            migrationBuilder.DropColumn(
                name: "PluginCombatLogEnabled",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "PluginRecordingEnabled",
                table: "RustServer");
        }
    }
}
