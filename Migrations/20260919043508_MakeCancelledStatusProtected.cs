using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Data-only migration: <c>Cancelled</c> was seeded as a plain, deletable status by
    /// <c>AddTicketStatusEntity</c>, but it's meant to be a permanent part of the lifecycle - see
    /// <c>TicketStatusSeeder</c>'s remarks. No schema change, since <c>TicketStatuses.IsProtected</c>
    /// already exists.
    /// </summary>
    public partial class MakeCancelledStatusProtected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"UPDATE ""TicketStatuses"" SET ""IsProtected"" = TRUE WHERE ""Slug"" = 'cancelled';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"UPDATE ""TicketStatuses"" SET ""IsProtected"" = FALSE WHERE ""Slug"" = 'cancelled';");
        }
    }
}
