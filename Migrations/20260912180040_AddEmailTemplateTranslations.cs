using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailTemplateTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailTemplateTranslation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Culture = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ModifiedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplateTranslation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailTemplateTranslation_EmailTemplate_EmailTemplateId",
                        column: x => x.EmailTemplateId,
                        principalTable: "EmailTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplateTranslation_EmailTemplateId_Culture",
                table: "EmailTemplateTranslation",
                columns: new[] { "EmailTemplateId", "Culture" },
                unique: true);

            // Backfill, before the source columns go away. Every existing template keeps exactly the
            // Subject/HtmlBody it already had, now as its "en-US" translation - the same culture
            // EmailTemplateRegistry.SeedCulture seeds a brand-new template with, so an existing
            // deployment and a fresh one end up in the same shape. Audit fields carried over from the
            // owning EmailTemplate row rather than stamped "now"/"nobody" - this content has existed
            // since the template itself was created, not since this migration ran.
            migrationBuilder.Sql("""
                INSERT INTO "EmailTemplateTranslation"
                    ("Id", "EmailTemplateId", "Culture", "Subject", "HtmlBody", "CreatedById", "CreatedOn")
                SELECT gen_random_uuid(),
                       t."Id",
                       'en-US',
                       t."Subject",
                       t."HtmlBody",
                       t."CreatedById",
                       t."CreatedOn"
                FROM "EmailTemplate" t;
                """);

            migrationBuilder.DropColumn(
                name: "HtmlBody",
                table: "EmailTemplate");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "EmailTemplate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HtmlBody",
                table: "EmailTemplate",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "EmailTemplate",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            // Mirrors Up's own backfill: restores each template's "en-US" translation back onto the
            // columns being reinstated, before the table holding it is dropped. Any other language
            // translated in the meantime is lost - the same one-way trip every column-to-child-table
            // migration in this codebase makes on rollback (see PlanPricing's Down for the precedent).
            migrationBuilder.Sql("""
                UPDATE "EmailTemplate" t
                SET "Subject" = tr."Subject",
                    "HtmlBody" = tr."HtmlBody"
                FROM "EmailTemplateTranslation" tr
                WHERE tr."EmailTemplateId" = t."Id" AND tr."Culture" = 'en-US';
                """);

            migrationBuilder.DropTable(
                name: "EmailTemplateTranslation");
        }
    }
}
