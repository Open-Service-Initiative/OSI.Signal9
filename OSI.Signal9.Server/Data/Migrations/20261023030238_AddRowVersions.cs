using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRowVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Tenants",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "TelemetrySamples",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Devices",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Commands",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Agents",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "TelemetrySamples");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Commands");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Agents");
        }
    }
}
