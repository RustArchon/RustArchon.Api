using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformSettingOrderingAndVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Options",
                table: "PlatformSetting",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "PlatformSetting",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VisibleWhenKey",
                table: "PlatformSetting",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VisibleWhenNegate",
                table: "PlatformSetting",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VisibleWhenValue",
                table: "PlatformSetting",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Options",
                table: "PlatformSetting");

            migrationBuilder.DropColumn(
                name: "Order",
                table: "PlatformSetting");

            migrationBuilder.DropColumn(
                name: "VisibleWhenKey",
                table: "PlatformSetting");

            migrationBuilder.DropColumn(
                name: "VisibleWhenNegate",
                table: "PlatformSetting");

            migrationBuilder.DropColumn(
                name: "VisibleWhenValue",
                table: "PlatformSetting");
        }
    }
}
