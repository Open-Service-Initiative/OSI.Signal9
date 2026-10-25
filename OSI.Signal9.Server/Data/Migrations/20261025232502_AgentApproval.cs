using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HoldsNewAgentsForApproval",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AwaitingApproval",
                table: "Agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HoldsNewAgentsForApproval",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AwaitingApproval",
                table: "Agents");
        }
    }
}
