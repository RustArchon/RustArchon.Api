using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscountCode",
                table: "Invoice",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountTotal",
                table: "Invoice",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "Discount",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AmountType = table.Column<int>(type: "integer", nullable: false),
                    AmountValue = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    MaxRedemptions = table.Column<int>(type: "integer", nullable: true),
                    TimesRedeemed = table.Column<int>(type: "integer", nullable: false),
                    OncePerOrganization = table.Column<bool>(type: "boolean", nullable: false),
                    RestrictedToTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Discount", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Discount_Tenant_RestrictedToTenantId",
                        column: x => x.RestrictedToTenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DiscountRedemption",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DiscountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RedeemedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiscountAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    AppliedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountRedemption", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscountRedemption_Discount_DiscountId",
                        column: x => x.DiscountId,
                        principalTable: "Discount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscountRedemption_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DiscountRedemption_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Discount_Code",
                table: "Discount",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Discount_RestrictedToTenantId",
                table: "Discount",
                column: "RestrictedToTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountRedemption_DiscountId",
                table: "DiscountRedemption",
                column: "DiscountId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountRedemption_InvoiceId",
                table: "DiscountRedemption",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountRedemption_TenantId",
                table: "DiscountRedemption",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscountRedemption");

            migrationBuilder.DropTable(
                name: "Discount");

            migrationBuilder.DropColumn(
                name: "DiscountCode",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "DiscountTotal",
                table: "Invoice");
        }
    }
}
