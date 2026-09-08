using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <summary>
    /// Renames <c>TenantPlan</c> to <c>Subscription</c> and <c>TenantPlanTerm</c> to
    /// <c>SubscriptionPeriod</c>, adds the subscription lifecycle status, and removes the two billing
    /// stamps that belong to invoices and payments rather than to a period. No behaviour change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Hand-written as renames.</strong> The scaffolded version dropped both tables and created
    /// the new ones - which is correct as a schema diff and catastrophic as a migration: it would have
    /// deleted every subscription interval and every billing period on the way past, taking the whole
    /// subscription and billing history with it. A rename is what "no behaviour change" actually means
    /// here, and the rows have to survive it.
    /// </para>
    /// <para>
    /// Postgres keeps constraint and index names when a table is renamed, so each of those is renamed
    /// explicitly too. Leaving them stale would work today and fail later, the first time a migration
    /// tried to alter one by the name EF now expects it to have.
    /// </para>
    /// </remarks>
    public partial class RenameSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "TenantPlan", newName: "Subscription");
            migrationBuilder.RenameTable(name: "TenantPlanTerm", newName: "SubscriptionPeriod");

            migrationBuilder.RenameColumn(
                name: "TenantPlanId", table: "SubscriptionPeriod", newName: "SubscriptionId");

            // Amount was ambiguous the moment invoices arrived: this is revenue earned, the invoice line
            // is money asked for, and they are neither the same figure nor dated the same.
            migrationBuilder.RenameColumn(
                name: "Amount", table: "SubscriptionPeriod", newName: "EarnedAmount");

            // Both always null - nothing has ever billed. Their replacements are Invoice.IssuedOn and
            // PaymentAllocation, which can express what a pair of timestamps here could not: partial
            // payment, several payments against one invoice, and a refund reversing one.
            migrationBuilder.DropColumn(name: "BilledOn", table: "SubscriptionPeriod");
            migrationBuilder.DropColumn(name: "PaidOn", table: "SubscriptionPeriod");

            // Every existing subscription is Active (0) - see SubscriptionStatus. Nothing moves it until
            // dunning exists.
            migrationBuilder.AddColumn<int>(
                name: "Status", table: "Subscription", type: "integer", nullable: false, defaultValue: 0);

            migrationBuilder.RenameIndex(
                name: "IX_TenantPlan_PlanId",
                newName: "IX_Subscription_PlanId",
                table: "Subscription");

            migrationBuilder.RenameIndex(
                name: "IX_TenantPlan_TenantId_StartDate",
                newName: "IX_Subscription_TenantId_StartDate",
                table: "Subscription");

            migrationBuilder.RenameIndex(
                name: "IX_TenantPlan_TenantId_WhereCurrent",
                newName: "IX_Subscription_TenantId_WhereCurrent",
                table: "Subscription");

            migrationBuilder.RenameIndex(
                name: "IX_TenantPlanTerm_TenantPlanId_StartDate",
                newName: "IX_SubscriptionPeriod_SubscriptionId_StartDate",
                table: "SubscriptionPeriod");

            // Primary and foreign keys: no MigrationBuilder verb renames a constraint, and renaming the
            // primary key's constraint renames its backing index with it.
            migrationBuilder.Sql("""
                ALTER TABLE "Subscription" RENAME CONSTRAINT "PK_TenantPlan" TO "PK_Subscription";
                ALTER TABLE "Subscription" RENAME CONSTRAINT "FK_TenantPlan_Plan_PlanId"
                    TO "FK_Subscription_Plan_PlanId";
                ALTER TABLE "Subscription" RENAME CONSTRAINT "FK_TenantPlan_Tenant_TenantId"
                    TO "FK_Subscription_Tenant_TenantId";
                ALTER TABLE "SubscriptionPeriod" RENAME CONSTRAINT "PK_TenantPlanTerm"
                    TO "PK_SubscriptionPeriod";
                ALTER TABLE "SubscriptionPeriod" RENAME CONSTRAINT "FK_TenantPlanTerm_TenantPlan_TenantPlanId"
                    TO "FK_SubscriptionPeriod_Subscription_SubscriptionId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "SubscriptionPeriod" RENAME CONSTRAINT "FK_SubscriptionPeriod_Subscription_SubscriptionId"
                    TO "FK_TenantPlanTerm_TenantPlan_TenantPlanId";
                ALTER TABLE "SubscriptionPeriod" RENAME CONSTRAINT "PK_SubscriptionPeriod"
                    TO "PK_TenantPlanTerm";
                ALTER TABLE "Subscription" RENAME CONSTRAINT "FK_Subscription_Tenant_TenantId"
                    TO "FK_TenantPlan_Tenant_TenantId";
                ALTER TABLE "Subscription" RENAME CONSTRAINT "FK_Subscription_Plan_PlanId"
                    TO "FK_TenantPlan_Plan_PlanId";
                ALTER TABLE "Subscription" RENAME CONSTRAINT "PK_Subscription" TO "PK_TenantPlan";
                """);

            migrationBuilder.RenameIndex(
                name: "IX_SubscriptionPeriod_SubscriptionId_StartDate",
                newName: "IX_TenantPlanTerm_TenantPlanId_StartDate",
                table: "SubscriptionPeriod");

            migrationBuilder.RenameIndex(
                name: "IX_Subscription_TenantId_WhereCurrent",
                newName: "IX_TenantPlan_TenantId_WhereCurrent",
                table: "Subscription");

            migrationBuilder.RenameIndex(
                name: "IX_Subscription_TenantId_StartDate",
                newName: "IX_TenantPlan_TenantId_StartDate",
                table: "Subscription");

            migrationBuilder.RenameIndex(
                name: "IX_Subscription_PlanId",
                newName: "IX_TenantPlan_PlanId",
                table: "Subscription");

            migrationBuilder.DropColumn(name: "Status", table: "Subscription");

            // Restored as null - the values they held were always null anyway, so nothing is lost.
            migrationBuilder.AddColumn<System.DateTimeOffset>(
                name: "PaidOn", table: "SubscriptionPeriod",
                type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<System.DateTimeOffset>(
                name: "BilledOn", table: "SubscriptionPeriod",
                type: "timestamp with time zone", nullable: true);

            migrationBuilder.RenameColumn(
                name: "EarnedAmount", table: "SubscriptionPeriod", newName: "Amount");
            migrationBuilder.RenameColumn(
                name: "SubscriptionId", table: "SubscriptionPeriod", newName: "TenantPlanId");

            migrationBuilder.RenameTable(name: "SubscriptionPeriod", newName: "TenantPlanTerm");
            migrationBuilder.RenameTable(name: "Subscription", newName: "TenantPlan");
        }
    }
}
