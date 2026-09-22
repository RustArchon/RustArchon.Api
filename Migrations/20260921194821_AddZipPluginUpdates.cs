using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddZipPluginUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ThirdPartyPluginUpdate",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "cs");

            migrationBuilder.AddColumn<bool>(
                name: "SaveMapping",
                table: "ThirdPartyPluginUpdate",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ZipSourceFindings",
                table: "PluginDownloadLookup",
                type: "text",
                nullable: true);

            // A zip checked before its plugin files were read has no findings: mark it not checked, so it is looked at again when somebody is waiting on it.
            // (0 = NotChecked. Its earlier hash and file list stay, so a file that changed under the same version is still noticed.)
            migrationBuilder.Sql("UPDATE \"PluginDownloadLookup\" SET \"ValidationState\" = 0 WHERE \"FileKind\" = 'zip'");

            migrationBuilder.CreateTable(
                name: "PluginZipMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RulesJson = table.Column<string>(type: "text", nullable: false),
                    Trusted = table.Column<bool>(type: "boolean", nullable: false),
                    SavedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAppliedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginZipMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginZipMapping_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginZipMapping_Server_Plugin",
                table: "PluginZipMapping",
                columns: new[] { "RustServerId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginZipMapping_TenantId",
                table: "PluginZipMapping",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginZipMapping");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ThirdPartyPluginUpdate");

            migrationBuilder.DropColumn(
                name: "SaveMapping",
                table: "ThirdPartyPluginUpdate");

            migrationBuilder.DropColumn(
                name: "ZipSourceFindings",
                table: "PluginDownloadLookup");
        }
    }
}
