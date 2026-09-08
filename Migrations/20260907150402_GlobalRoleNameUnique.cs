using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Enforces "at most one global role per name", which JumpStart's own
    /// <c>UNIQUE (TenantId, Name)</c> index does not: Postgres treats NULLs as distinct, so nothing
    /// stopped a second role named "Owner" with a null tenant - precisely the shape the built-in
    /// roles use.
    /// </summary>
    /// <remarks>
    /// Index only: no data change, and reversible. Creating a unique index fails loudly if
    /// duplicates already exist, which is the right outcome - two global roles sharing a name is the
    /// state this exists to make impossible, and deciding which is real needs a human. Checked by
    /// hand like the rest of this project's migrations.
    /// </remarks>
    public partial class GlobalRoleNameUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Role_Name_WhereGlobal",
                table: "Roles",
                column: "Name",
                unique: true,
                filter: "\"TenantId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Role_Name_WhereGlobal",
                table: "Roles");
        }
    }
}
