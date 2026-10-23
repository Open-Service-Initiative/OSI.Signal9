using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeparateDevicesFromAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Agent-era rows don't carry over: agents re-register and become devices. Breaking changes are expected
            // before 1.0 (#63), and the new foreign keys would reject the old agent IDs.
            migrationBuilder.Sql("""DELETE FROM "TelemetrySamples"; DELETE FROM "AgentCommands"; DELETE FROM "Agents";""");

            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Tenants_TenantId",
                table: "Agents");

            migrationBuilder.DropForeignKey(
                name: "FK_TelemetrySamples_Agents_AgentId",
                table: "TelemetrySamples");

            migrationBuilder.DropTable(
                name: "AgentCommands");

            migrationBuilder.DropIndex(
                name: "IX_Agents_LastSeenAt",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Agents_TenantId_MachineName",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "Architecture",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InMaintenance",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MacAddress",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MachineName",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "OperatingSystem",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "OsVersion",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ProcessorCores",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ProcessorName",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "TotalMemoryMb",
                table: "Agents");

            migrationBuilder.RenameColumn(
                name: "MaxAgents",
                table: "Tenants",
                newName: "MaxDevices");

            migrationBuilder.RenameColumn(
                name: "AgentId",
                table: "TelemetrySamples",
                newName: "DeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_TelemetrySamples_AgentId_CollectedAt",
                table: "TelemetrySamples",
                newName: "IX_TelemetrySamples_DeviceId_CollectedAt");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                table: "Agents",
                newName: "DeviceId");

            migrationBuilder.RenameColumn(
                name: "FirstSeenAt",
                table: "Agents",
                newName: "EnrolledAt");

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveAgentId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    GroupName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    MaintenanceMode = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Agents_ActiveAgentId",
                        column: x => x.ActiveAgentId,
                        principalTable: "Agents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Devices_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Parameters = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Output = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Commands_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_DeviceId",
                table: "Agents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Commands_DeviceId_Status",
                table: "Commands",
                columns: new[] { "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ActiveAgentId",
                table: "Devices",
                column: "ActiveAgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId_MachineName",
                table: "Devices",
                columns: new[] { "TenantId", "MachineName" });

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Devices_DeviceId",
                table: "Agents",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TelemetrySamples_Devices_DeviceId",
                table: "TelemetrySamples",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Devices_DeviceId",
                table: "Agents");

            migrationBuilder.DropForeignKey(
                name: "FK_TelemetrySamples_Devices_DeviceId",
                table: "TelemetrySamples");

            migrationBuilder.DropTable(
                name: "Commands");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Agents_DeviceId",
                table: "Agents");

            migrationBuilder.RenameColumn(
                name: "MaxDevices",
                table: "Tenants",
                newName: "MaxAgents");

            migrationBuilder.RenameColumn(
                name: "DeviceId",
                table: "TelemetrySamples",
                newName: "AgentId");

            migrationBuilder.RenameIndex(
                name: "IX_TelemetrySamples_DeviceId_CollectedAt",
                table: "TelemetrySamples",
                newName: "IX_TelemetrySamples_AgentId_CollectedAt");

            migrationBuilder.RenameColumn(
                name: "EnrolledAt",
                table: "Agents",
                newName: "FirstSeenAt");

            migrationBuilder.RenameColumn(
                name: "DeviceId",
                table: "Agents",
                newName: "TenantId");

            migrationBuilder.AddColumn<string>(
                name: "Architecture",
                table: "Agents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "Agents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "Agents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InMaintenance",
                table: "Agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "Agents",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacAddress",
                table: "Agents",
                type: "character varying(17)",
                maxLength: 17,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineName",
                table: "Agents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OperatingSystem",
                table: "Agents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                table: "Agents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProcessorCores",
                table: "Agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProcessorName",
                table: "Agents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Tags",
                table: "Agents",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<long>(
                name: "TotalMemoryMb",
                table: "Agents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "AgentCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Output = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    Parameters = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCommands_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_LastSeenAt",
                table: "Agents",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_MachineName",
                table: "Agents",
                columns: new[] { "TenantId", "MachineName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCommands_AgentId_Status",
                table: "AgentCommands",
                columns: new[] { "AgentId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Tenants_TenantId",
                table: "Agents",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TelemetrySamples_Agents_AgentId",
                table: "TelemetrySamples",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
