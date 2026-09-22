using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddThirdPartyPluginUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThirdPartyPluginUpdate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClassName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ToVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PluginDownloadLookupId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActualSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThirdPartyPluginUpdate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ThirdPartyPluginUpdate_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThirdPartyPluginUpdate_Server_Plugin_Version",
                table: "ThirdPartyPluginUpdate",
                columns: new[] { "RustServerId", "NormalizedName", "ToVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_ThirdPartyPluginUpdate_Server_StartedAtUtc",
                table: "ThirdPartyPluginUpdate",
                columns: new[] { "RustServerId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ThirdPartyPluginUpdate_TenantId",
                table: "ThirdPartyPluginUpdate",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThirdPartyPluginUpdate");
        }
    }
}
