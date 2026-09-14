using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RustArchon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRconEventInteractivityAndDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 1 (RconEventDirection.Received), not the tool-generated 0 (Sent): sent-command
            // capture is new as of this migration - every existing row is a response or unsolicited
            // output, never a sent command, since no code path persisted one before now.
            migrationBuilder.AddColumn<int>(
                name: "Direction",
                table: "RconEvent",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Default true, not the tool-generated false: every RconEvent row that already exists was
            // captured under the old suppress-at-source model, which never persisted a non-interactive
            // response in the first place (see RconFrameCaptured's remarks) - so every existing row
            // genuinely was interactive.
            migrationBuilder.AddColumn<bool>(
                name: "Interactive",
                table: "RconEvent",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Direction",
                table: "RconEvent");

            migrationBuilder.DropColumn(
                name: "Interactive",
                table: "RconEvent");
        }
    }
}
