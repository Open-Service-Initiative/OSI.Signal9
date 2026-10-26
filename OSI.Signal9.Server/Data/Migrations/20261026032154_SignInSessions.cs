using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignInSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EndedEntraSessions",
                columns: table => new
                {
                    EntraTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraSessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndedEntraSessions", x => new { x.EntraTenantId, x.EntraSessionId });
                });

            migrationBuilder.CreateTable(
                name: "EntraTokenCache",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Value = table.Column<byte[]>(type: "bytea", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntraTokenCache", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SignInSessions",
                columns: table => new
                {
                    AuthorizationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntraTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraSessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EntraAccountId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Superadministrator = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SignedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EntraCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInSessions", x => x.AuthorizationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignInSessions_EntraAccountId_EntraCheckedAt",
                table: "SignInSessions",
                columns: new[] { "EntraAccountId", "EntraCheckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignInSessions_EntraTenantId_EntraSessionId",
                table: "SignInSessions",
                columns: new[] { "EntraTenantId", "EntraSessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignInSessions_Subject",
                table: "SignInSessions",
                column: "Subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndedEntraSessions");

            migrationBuilder.DropTable(
                name: "EntraTokenCache");

            migrationBuilder.DropTable(
                name: "SignInSessions");
        }
    }
}
