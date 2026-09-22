using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantThirdPartyPluginUpdatesToHqmAndGold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Scott's decision (2026-09-21): automatic third-party plugin updates are an HQM differentiator, and Gold (Comped) gets it too.
            // Every row with those names, not only the active ones: a plan someone is subscribed to cannot be edited in place, and superseding it
            // would leave its current subscribers on the old row without the feature - so an entitlement that only adds something is set here.
            migrationBuilder.Sql("""UPDATE "Plan" SET "OffersThirdPartyPluginUpdates" = TRUE WHERE "Name" IN ('HQM', 'Gold (Comped)');""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Plan" SET "OffersThirdPartyPluginUpdates" = FALSE WHERE "Name" IN ('HQM', 'Gold (Comped)');""");
        }
    }
}
