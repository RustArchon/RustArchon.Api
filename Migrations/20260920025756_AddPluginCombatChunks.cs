using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginCombatChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginCombatChunk",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RustServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BootId = table.Column<long>(type: "bigint", nullable: false),
                    FirstSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    FromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    PlayerIds = table.Column<string[]>(type: "text[]", nullable: false),
                    PrecededByGap = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginCombatChunk", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginCombatChunk_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginCombatChunk_Server_Boot_FirstSequence",
                table: "PluginCombatChunk",
                columns: new[] { "RustServerId", "BootId", "FirstSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginCombatChunk_Server_ToUtc",
                table: "PluginCombatChunk",
                columns: new[] { "RustServerId", "ToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginCombatChunk_TenantId",
                table: "PluginCombatChunk",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginCombatChunk_ToUtc",
                table: "PluginCombatChunk",
                column: "ToUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginCombatChunk");
        }
    }
}
