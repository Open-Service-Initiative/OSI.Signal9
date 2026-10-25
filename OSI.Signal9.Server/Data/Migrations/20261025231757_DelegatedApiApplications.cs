using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DelegatedApiApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                table: "ApiApplications",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ApiApplications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Every application registered before #125 is app-only.
                defaultValue: "app-only");

            migrationBuilder.AddColumn<string[]>(
                name: "RedirectUris",
                table: "ApiApplications",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<bool>(
                name: "RequireNonce",
                table: "ApiApplications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequirePkce",
                table: "ApiApplications",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ApiApplications");

            migrationBuilder.DropColumn(
                name: "RedirectUris",
                table: "ApiApplications");

            migrationBuilder.DropColumn(
                name: "RequireNonce",
                table: "ApiApplications");

            migrationBuilder.DropColumn(
                name: "RequirePkce",
                table: "ApiApplications");

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                table: "ApiApplications",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
