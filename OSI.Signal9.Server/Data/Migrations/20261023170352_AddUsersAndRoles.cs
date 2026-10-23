using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersAndRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Scopes = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "BuiltIn", "Description", "Name", "Scopes" },
                values: new object[,]
                {
                    { new Guid("0199a000-0000-7000-8000-000000000001"), true, "Read-only access to tenants, devices, commands, scripts, telemetry and users.", "Viewer", new[] { "tenants.read", "devices.read", "commands.read", "scripts.read", "telemetry.read", "users.read" } },
                    { new Guid("0199a000-0000-7000-8000-000000000002"), true, "Viewer, plus operating devices: running commands and scripts, enrolling devices and managing users without roles.", "Technician", new[] { "tenants.read", "devices.read", "commands.read", "scripts.read", "telemetry.read", "users.read", "devices.write", "devices.transfer", "commands.execute", "scripts.write", "scripts.delete", "scripts.execute", "terminal.execute", "users.write", "enrollment.read", "enrollment.write" } },
                    { new Guid("0199a000-0000-7000-8000-000000000003"), true, "Technician, plus administering tenants, role assignments, API applications, integrations and the audit log.", "Administrator", new[] { "tenants.read", "devices.read", "commands.read", "scripts.read", "telemetry.read", "users.read", "devices.write", "devices.transfer", "commands.execute", "scripts.write", "scripts.delete", "scripts.execute", "terminal.execute", "users.write", "enrollment.read", "enrollment.write", "tenants.write", "tenants.delete", "tenants.transfer", "devices.delete", "roleassignments.read", "roleassignments.write", "roles.read", "roles.write", "audit.read", "apiapplications.read", "apiapplications.write", "integrations.read", "integrations.write", "users.transfer" } }
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_TenantId",
                table: "RoleAssignments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_UserId_TenantId_RoleId",
                table: "RoleAssignments",
                columns: new[] { "UserId", "TenantId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_EntraTenantId_EntraObjectId",
                table: "Users",
                columns: new[] { "EntraTenantId", "EntraObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                table: "Users",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
