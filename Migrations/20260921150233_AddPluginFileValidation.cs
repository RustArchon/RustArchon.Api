using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginFileValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FileHashChanges",
                table: "PluginDownloadLookup",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FileKind",
                table: "PluginDownloadLookup",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileSha256",
                table: "PluginDownloadLookup",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FileSizeBytes",
                table: "PluginDownloadLookup",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginClassName",
                table: "PluginDownloadLookup",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginInfoAuthor",
                table: "PluginDownloadLookup",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginInfoName",
                table: "PluginDownloadLookup",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginInfoVersion",
                table: "PluginDownloadLookup",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousFileSha256",
                table: "PluginDownloadLookup",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ValidatedAtUtc",
                table: "PluginDownloadLookup",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidationAttempts",
                table: "PluginDownloadLookup",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ValidationNextAttemptUtc",
                table: "PluginDownloadLookup",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationReason",
                table: "PluginDownloadLookup",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidationState",
                table: "PluginDownloadLookup",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ZipEntries",
                table: "PluginDownloadLookup",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileHashChanges",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "FileKind",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "FileSha256",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "FileSizeBytes",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "PluginClassName",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "PluginInfoAuthor",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "PluginInfoName",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "PluginInfoVersion",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "PreviousFileSha256",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "ValidatedAtUtc",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "ValidationAttempts",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "ValidationNextAttemptUtc",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "ValidationReason",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "ValidationState",
                table: "PluginDownloadLookup");

            migrationBuilder.DropColumn(
                name: "ZipEntries",
                table: "PluginDownloadLookup");
        }
    }
}
