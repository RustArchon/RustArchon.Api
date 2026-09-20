using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginKeyHistoryAndReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SigningKeyFingerprint",
                table: "PluginUpdateToken",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PluginAdminEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAdminEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PluginKeyHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ModulusBase64 = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ExponentBase64 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EncryptedPrivateKey = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RetiredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginKeyHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PluginRelease",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceText = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WithdrawnAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WithdrawnReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginRelease", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAdminEvent_AtUtc",
                table: "PluginAdminEvent",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PluginKeyHistory_Fingerprint",
                table: "PluginKeyHistory",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginRelease_Kind_Version",
                table: "PluginRelease",
                columns: new[] { "Kind", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginAdminEvent");

            migrationBuilder.DropTable(
                name: "PluginKeyHistory");

            migrationBuilder.DropTable(
                name: "PluginRelease");

            migrationBuilder.DropColumn(
                name: "SigningKeyFingerprint",
                table: "PluginUpdateToken");
        }
    }
}
