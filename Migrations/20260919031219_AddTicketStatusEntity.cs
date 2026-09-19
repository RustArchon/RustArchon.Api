using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketStatusEntity : Migration
    {
        // Fixed, hand-picked ids for the six starter statuses this migration inserts directly (rather
        // than leaving that to TicketStatusSeeder's own startup pass) - a migration is the only place
        // that can also backfill every existing Ticket.StatusId from the int enum values (Submitted=0,
        // Open=1, WaitingOnCustomer=2, Resolved=3, Closed=4 - see the enum this migration removes) this
        // same migration is dropping. TicketStatusSeeder is gated on "no TicketStatus rows exist yet",
        // so it no-ops after this runs, on both an upgrade and a fresh install.
        private static readonly Guid SubmittedStatusId = new("10000000-0000-0000-0000-000000000001");
        private static readonly Guid OpenStatusId = new("10000000-0000-0000-0000-000000000002");
        private static readonly Guid WaitingOnCustomerStatusId = new("10000000-0000-0000-0000-000000000003");
        private static readonly Guid ResolvedStatusId = new("10000000-0000-0000-0000-000000000004");
        private static readonly Guid ClosedStatusId = new("10000000-0000-0000-0000-000000000005");
        private static readonly Guid CancelledStatusId = new("10000000-0000-0000-0000-000000000006");
        private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StatusId",
                table: "Tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TicketStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    IsProtected = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ModifiedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketStatuses", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TicketStatuses",
                columns: new[]
                {
                    "Id", "Slug", "Name", "IsClosed", "IsProtected", "IsActive", "DisplayOrder", "CreatedById",
                    "CreatedOn"
                },
                values: new object[,]
                {
                    { SubmittedStatusId, "submitted", "Submitted", false, true, true, 10, Guid.Empty, SeedTimestamp },
                    { OpenStatusId, "open", "Open", false, true, true, 20, Guid.Empty, SeedTimestamp },
                    {
                        WaitingOnCustomerStatusId, "waiting-on-customer", "Waiting on Customer", false, true, true,
                        30, Guid.Empty, SeedTimestamp
                    },
                    { ResolvedStatusId, "resolved", "Resolved", false, true, true, 40, Guid.Empty, SeedTimestamp },
                    { ClosedStatusId, "closed", "Closed", true, true, true, 50, Guid.Empty, SeedTimestamp },
                    { CancelledStatusId, "cancelled", "Cancelled", true, false, true, 60, Guid.Empty, SeedTimestamp }
                });

            migrationBuilder.Sql($@"
                UPDATE ""Tickets"" SET ""StatusId"" = CASE ""Status""
                    WHEN 0 THEN '{SubmittedStatusId}'::uuid
                    WHEN 1 THEN '{OpenStatusId}'::uuid
                    WHEN 2 THEN '{WaitingOnCustomerStatusId}'::uuid
                    WHEN 3 THEN '{ResolvedStatusId}'::uuid
                    WHEN 4 THEN '{ClosedStatusId}'::uuid
                    ELSE '{SubmittedStatusId}'::uuid
                END;");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Tickets");

            migrationBuilder.AlterColumn<Guid>(
                name: "StatusId",
                table: "Tickets",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ticket_StatusId",
                table: "Tickets",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketStatus_Slug",
                table: "TicketStatuses",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_TicketStatuses_StatusId",
                table: "Tickets",
                column: "StatusId",
                principalTable: "TicketStatuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_TicketStatuses_StatusId",
                table: "Tickets");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql($@"
                UPDATE ""Tickets"" SET ""Status"" = CASE ""StatusId""
                    WHEN '{SubmittedStatusId}'::uuid THEN 0
                    WHEN '{OpenStatusId}'::uuid THEN 1
                    WHEN '{WaitingOnCustomerStatusId}'::uuid THEN 2
                    WHEN '{ResolvedStatusId}'::uuid THEN 3
                    WHEN '{ClosedStatusId}'::uuid THEN 4
                    ELSE 0
                END;");

            migrationBuilder.DropIndex(
                name: "IX_Ticket_StatusId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_TicketStatus_Slug",
                table: "TicketStatuses");

            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "TicketStatuses");
        }
    }
}
