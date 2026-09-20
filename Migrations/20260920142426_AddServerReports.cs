using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddServerReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReportForwardingVerifiedAtUtc",
                table: "RustServer",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportsSecret",
                table: "RustServer",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServerReport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReporterSteamId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReporterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TargetSteamId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TargetName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    Position = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MinutesPlayed = table.Column<int>(type: "integer", nullable: true),
                    ScreenshotObjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ScreenshotBytes = table.Column<long>(type: "bigint", nullable: true),
                    PluginDetailJson = table.Column<string>(type: "text", nullable: true),
                    NativePayload = table.Column<string>(type: "text", nullable: true),
                    PluginPayload = table.Column<string>(type: "text", nullable: true),
                    ParseFailed = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerReport_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerReport_TenantId_RustServerId_ReceivedAtUtc",
                table: "ServerReport",
                columns: new[] { "TenantId", "RustServerId", "ReceivedAtUtc" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerReport");

            migrationBuilder.DropColumn(
                name: "ReportForwardingVerifiedAtUtc",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "ReportsSecret",
                table: "RustServer");
        }
    }
}
