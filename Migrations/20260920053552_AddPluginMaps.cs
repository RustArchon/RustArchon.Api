using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginMap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorldSize = table.Column<int>(type: "integer", nullable: false),
                    WorldSeed = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExistsOnServer = table.Column<bool>(type: "boolean", nullable: false),
                    ServerBytes = table.Column<long>(type: "bigint", nullable: false),
                    MonumentsJson = table.Column<string>(type: "text", nullable: true),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UploadRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ObjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginMap", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginMap_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PluginMapUploadToken",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginMapId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginMapUploadToken", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginMapUploadToken_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginMap_Server_World",
                table: "PluginMap",
                columns: new[] { "RustServerId", "WorldSize", "WorldSeed" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginMap_TenantId",
                table: "PluginMap",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginMapUploadToken_ExpiresAtUtc",
                table: "PluginMapUploadToken",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PluginMapUploadToken_TenantId",
                table: "PluginMapUploadToken",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginMapUploadToken_TokenHash",
                table: "PluginMapUploadToken",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginMap");

            migrationBuilder.DropTable(
                name: "PluginMapUploadToken");
        }
    }
}
