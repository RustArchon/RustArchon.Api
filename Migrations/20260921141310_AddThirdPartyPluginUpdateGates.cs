using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddThirdPartyPluginUpdateGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ThirdPartyPluginUpdateHoldDays",
                table: "RustServer",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<bool>(
                name: "ThirdPartyPluginUpdatesEnabled",
                table: "RustServer",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OffersThirdPartyPluginUpdates",
                table: "Plan",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThirdPartyPluginUpdateHoldDays",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "ThirdPartyPluginUpdatesEnabled",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "OffersThirdPartyPluginUpdates",
                table: "Plan");
        }
    }
}
