using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeConnectionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Detail",
                table: "ConnectionLogEntry");

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "ConnectionLogEntry",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "ConnectionLogEntry",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "ConnectionLogEntry",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Level",
                table: "ConnectionLogEntry");

            migrationBuilder.DropColumn(
                name: "Message",
                table: "ConnectionLogEntry");

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "ConnectionLogEntry",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "ConnectionLogEntry",
                type: "text",
                nullable: true);
        }
    }
}
