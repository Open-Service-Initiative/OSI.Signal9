using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentOsFamily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OsFamily",
                table: "PendingIdentifications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Agents ran only on Windows before the family was recorded.
                defaultValue: "Windows");

            migrationBuilder.AddColumn<string>(
                name: "OsFamily",
                table: "Devices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Agents ran only on Windows before the family was recorded.
                defaultValue: "Windows");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OsFamily",
                table: "PendingIdentifications");

            migrationBuilder.DropColumn(
                name: "OsFamily",
                table: "Devices");
        }
    }
}
