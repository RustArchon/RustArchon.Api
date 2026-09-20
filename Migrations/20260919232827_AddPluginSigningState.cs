using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginSigningState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SigningKeyFingerprint",
                table: "ServerPluginStatus",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SigningState",
                table: "ServerPluginStatus",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SigningKeyFingerprint",
                table: "ServerPluginStatus");

            migrationBuilder.DropColumn(
                name: "SigningState",
                table: "ServerPluginStatus");
        }
    }
}
