using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerKillEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VictimName = table.Column<string>(type: "text", nullable: false),
                    VictimSteamId = table.Column<string>(type: "text", nullable: true),
                    KillerName = table.Column<string>(type: "text", nullable: true),
                    KillerSteamId = table.Column<string>(type: "text", nullable: true),
                    Weapon = table.Column<string>(type: "text", nullable: true),
                    RawMessage = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerKillEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerKillEvent_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SteamId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    IpAddress = table.Column<string>(type: "text", nullable: false),
                    ConnectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisconnectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GeolocationProvider = table.Column<string>(type: "text", nullable: true),
                    GeolocationCountry = table.Column<string>(type: "text", nullable: true),
                    GeolocationCountryCode = table.Column<string>(type: "text", nullable: true),
                    GeolocationIsVpn = table.Column<bool>(type: "boolean", nullable: true),
                    GeolocationIsProxy = table.Column<bool>(type: "boolean", nullable: true),
                    GeolocationCheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerSession_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerKillEvent_TenantId_RustServerId_OccurredAtUtc",
                table: "PlayerKillEvent",
                columns: new[] { "TenantId", "RustServerId", "OccurredAtUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSession_TenantId_RustServerId_ConnectedAtUtc",
                table: "PlayerSession",
                columns: new[] { "TenantId", "RustServerId", "ConnectedAtUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSession_TenantId_RustServerId_SteamId_DisconnectedAtUtc",
                table: "PlayerSession",
                columns: new[] { "TenantId", "RustServerId", "SteamId", "DisconnectedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerKillEvent");

            migrationBuilder.DropTable(
                name: "PlayerSession");
        }
    }
}
