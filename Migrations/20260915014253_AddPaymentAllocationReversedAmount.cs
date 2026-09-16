using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAllocationReversedAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReversedAmount",
                table: "PaymentAllocation",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Backfill: every allocation already marked fully reversed under the old (ReversedOn-only)
            // model was reversed for its whole Amount, by definition - that was the only reversal shape
            // that existed before this column. Leaving these at the column's own 0m default would make
            // a previously-reversed allocation look "live" again to the new ReversedAmount < Amount
            // check that ReversePaymentAsync/GetInvoicesAsync now use.
            migrationBuilder.Sql(
                """UPDATE "PaymentAllocation" SET "ReversedAmount" = "Amount" WHERE "ReversedOn" IS NOT NULL""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReversedAmount",
                table: "PaymentAllocation");
        }
    }
}
