using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class MoveTicketPreventReopeningToTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreventReopening",
                table: "TicketStatuses");

            migrationBuilder.AddColumn<bool>(
                name: "PreventReopening",
                table: "Tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreventReopening",
                table: "Tickets");

            migrationBuilder.AddColumn<bool>(
                name: "PreventReopening",
                table: "TicketStatuses",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
