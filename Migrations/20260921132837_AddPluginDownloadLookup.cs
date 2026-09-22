using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginDownloadLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginDownloadLookup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    DownloadUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MatchedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MatchedPageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MatchedVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResponseJson = table.Column<string>(type: "text", nullable: true),
                    ResponseTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginDownloadLookup", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginDownloadLookup_Key",
                table: "PluginDownloadLookup",
                columns: new[] { "MarketplaceKey", "NormalizedName", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginDownloadLookup_NextAttemptUtc",
                table: "PluginDownloadLookup",
                column: "NextAttemptUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginDownloadLookup");
        }
    }
}
