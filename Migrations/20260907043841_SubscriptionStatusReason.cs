using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Records why a subscription's status last moved, for the suspension actions in the Organizations
    /// console - see <c>IOrganizationLifecycleService</c>.
    /// </summary>
    /// <remarks>
    /// Purely additive: two nullable columns, no defaults, no backfill. Every existing subscription is
    /// Active and has never moved, so "no reason recorded" is the honest state for all of them rather
    /// than something to invent a value for. Checked by hand like the rest of this project's migrations -
    /// the scaffolder has produced a destructive plan here before.
    /// </remarks>
    public partial class SubscriptionStatusReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedOn",
                table: "Subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                table: "Subscription",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StatusChangedOn",
                table: "Subscription");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                table: "Subscription");
        }
    }
}
