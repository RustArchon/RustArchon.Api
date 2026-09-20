using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PluginUpdatesEnabled",
                table: "RustServer",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PluginUpdateToken",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginUpdateToken", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginUpdateToken_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginUpdateToken_ExpiresAtUtc",
                table: "PluginUpdateToken",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PluginUpdateToken_TenantId",
                table: "PluginUpdateToken",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginUpdateToken_TokenHash",
                table: "PluginUpdateToken",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginUpdateToken");

            migrationBuilder.DropColumn(
                name: "PluginUpdatesEnabled",
                table: "RustServer");
        }
    }
}
