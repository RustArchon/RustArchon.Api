using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginMapPreview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PreviewBytes",
                table: "PluginMap",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewObjectKey",
                table: "PluginMap",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewSha256",
                table: "PluginMap",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviewBytes",
                table: "PluginMap");

            migrationBuilder.DropColumn(
                name: "PreviewObjectKey",
                table: "PluginMap");

            migrationBuilder.DropColumn(
                name: "PreviewSha256",
                table: "PluginMap");
        }
    }
}
