using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DashboardSessionRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndedAt",
                table: "DashboardSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevocationAttempts",
                table: "DashboardSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevocationDueAt",
                table: "DashboardSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DashboardSessions_RevocationDueAt",
                table: "DashboardSessions",
                column: "RevocationDueAt",
                filter: "\"EndedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DashboardSessions_RevocationDueAt",
                table: "DashboardSessions");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "DashboardSessions");

            migrationBuilder.DropColumn(
                name: "RevocationAttempts",
                table: "DashboardSessions");

            migrationBuilder.DropColumn(
                name: "RevocationDueAt",
                table: "DashboardSessions");
        }
    }
}
