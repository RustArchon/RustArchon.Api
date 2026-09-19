using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketReopenedStatusAndPreventReopening : Migration
    {
        // Same fixed-id convention as AddTicketStatusEntity's own starter rows, for the same reason -
        // this is the one place that can both add the row and be certain of its id, and
        // TicketStatusSeeder is gated on "no TicketStatus rows exist yet" so it never runs again on an
        // upgraded database to seed this one itself.
        private static readonly Guid ReopenedStatusId = new("10000000-0000-0000-0000-000000000007");
        private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PreventReopening",
                table: "TicketStatuses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.InsertData(
                table: "TicketStatuses",
                columns: new[]
                {
                    "Id", "Slug", "Name", "IsClosed", "IsProtected", "PreventReopening", "IsActive",
                    "DisplayOrder", "CreatedById", "CreatedOn"
                },
                values: new object[]
                {
                    ReopenedStatusId, "reopened", "Reopened", false, true, false, true, 25, Guid.Empty,
                    SeedTimestamp
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "TicketStatuses",
                keyColumn: "Id",
                keyValue: ReopenedStatusId);

            migrationBuilder.DropColumn(
                name: "PreventReopening",
                table: "TicketStatuses");
        }
    }
}
