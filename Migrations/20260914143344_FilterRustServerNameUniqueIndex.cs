using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class FilterRustServerNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RustServer_TenantId_Name",
                table: "RustServer");

            migrationBuilder.CreateIndex(
                name: "IX_RustServer_TenantId_Name",
                table: "RustServer",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"DeletedOn\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RustServer_TenantId_Name",
                table: "RustServer");

            migrationBuilder.CreateIndex(
                name: "IX_RustServer_TenantId_Name",
                table: "RustServer",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
