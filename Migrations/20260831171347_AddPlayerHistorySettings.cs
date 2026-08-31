using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerHistorySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GeolocationApiKey",
                table: "RustServer",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GeolocationProvider",
                table: "RustServer",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SteamApiKey",
                table: "RustServer",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastPing",
                table: "PlayerSession",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastViolationLevel",
                table: "PlayerSession",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SteamInfoCheckedAtUtc",
                table: "PlayerSession",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SteamMinutesPlayedForever",
                table: "PlayerSession",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SteamNumberOfGameBans",
                table: "PlayerSession",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SteamNumberOfVacBans",
                table: "PlayerSession",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SteamVacBanned",
                table: "PlayerSession",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GeolocationApiKey",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "GeolocationProvider",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "SteamApiKey",
                table: "RustServer");

            migrationBuilder.DropColumn(
                name: "LastPing",
                table: "PlayerSession");

            migrationBuilder.DropColumn(
                name: "LastViolationLevel",
                table: "PlayerSession");

            migrationBuilder.DropColumn(
                name: "SteamInfoCheckedAtUtc",
                table: "PlayerSession");

            migrationBuilder.DropColumn(
                name: "SteamMinutesPlayedForever",
                table: "PlayerSession");

            migrationBuilder.DropColumn(
                name: "SteamNumberOfGameBans",
                table: "PlayerSession");

            migrationBuilder.DropColumn(
                name: "SteamNumberOfVacBans",
                table: "PlayerSession");

            migrationBuilder.DropColumn(
                name: "SteamVacBanned",
                table: "PlayerSession");
        }
    }
}
