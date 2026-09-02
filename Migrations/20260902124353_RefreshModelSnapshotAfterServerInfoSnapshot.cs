using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Deliberately empty. <c>AddServerInfoSnapshot</c>'s own generated snapshot already matched the
    /// runtime model exactly (confirmed: re-running <c>dotnet ef migrations add</c> right after it
    /// produced an empty diff) - yet starting the app against it still threw
    /// <c>PendingModelChangesWarning</c> at the <c>Migrate()</c> pre-check, before that migration was
    /// ever applied. A known EF Core tooling quirk, not a real schema difference: the fix is exactly
    /// this - one more (even empty) migration, which resolved it. See <c>AddServerInfoSnapshot</c>.
    /// </summary>
    public partial class RefreshModelSnapshotAfterServerInfoSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
