using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentIdentification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Manufacturer",
                table: "Devices",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SerialNumber",
                table: "Devices",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SmbiosUuid",
                table: "Devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "DeviceId",
                table: "Agents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "PendingIdentifications",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OperatingSystem = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OsVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Architecture = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ProcessorName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ProcessorCores = table.Column<int>(type: "integer", nullable: false),
                    TotalMemoryMb = table.Column<long>(type: "bigint", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    MacAddress = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    SmbiosUuid = table.Column<Guid>(type: "uuid", nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingIdentifications", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_PendingIdentifications_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingIdentifications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SerialNumber_Manufacturer",
                table: "Devices",
                columns: new[] { "SerialNumber", "Manufacturer" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SmbiosUuid",
                table: "Devices",
                column: "SmbiosUuid");

            migrationBuilder.CreateIndex(
                name: "IX_PendingIdentifications_TenantId",
                table: "PendingIdentifications",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingIdentifications");

            migrationBuilder.DropIndex(
                name: "IX_Devices_SerialNumber_Manufacturer",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_SmbiosUuid",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "Manufacturer",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SmbiosUuid",
                table: "Devices");

            // Agents still awaiting identification have no device to keep them.
            migrationBuilder.Sql("""DELETE FROM "Agents" WHERE "DeviceId" IS NULL;""");

            migrationBuilder.AlterColumn<Guid>(
                name: "DeviceId",
                table: "Agents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
