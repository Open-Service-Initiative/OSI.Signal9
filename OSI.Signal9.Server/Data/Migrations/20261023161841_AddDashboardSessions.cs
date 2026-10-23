using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DashboardSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProtectedTokens = table.Column<byte[]>(type: "bytea", nullable: false),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardSessions_Subject",
                table: "DashboardSessions",
                column: "Subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardSessions");
        }
    }
}
