using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddThemeUpdateCheckState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastUpdateCheckError",
                table: "Themes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUpdateCheckOn",
                table: "Themes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestDownloadPackageUrl",
                table: "Themes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestKnownVersion",
                table: "Themes",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUpdateCheckError",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "LastUpdateCheckOn",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "LatestDownloadPackageUrl",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "LatestKnownVersion",
                table: "Themes");
        }
    }
}
