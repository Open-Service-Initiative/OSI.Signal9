using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantClosures",
                columns: table => new
                {
                    AncestorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DescendantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantClosures", x => new { x.AncestorId, x.DescendantId });
                    table.ForeignKey(
                        name: "FK_TenantClosures_Tenants_AncestorId",
                        column: x => x.AncestorId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TenantClosures_Tenants_DescendantId",
                        column: x => x.DescendantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantClosures_DescendantId",
                table: "TenantClosures",
                column: "DescendantId");

            // Derive closure rows for tenants that already exist from their parent pointers.
            migrationBuilder.Sql("""
                WITH RECURSIVE closure AS (
                    SELECT "Id" AS ancestor, "Id" AS descendant, 0 AS depth FROM "Tenants"
                    UNION ALL
                    SELECT c.ancestor, t."Id", c.depth + 1
                    FROM closure c JOIN "Tenants" t ON t."ParentTenantId" = c.descendant
                )
                INSERT INTO "TenantClosures" ("AncestorId", "DescendantId", "Depth")
                SELECT ancestor, descendant, depth FROM closure;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantClosures");
        }
    }
}
