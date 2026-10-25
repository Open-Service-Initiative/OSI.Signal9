using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedAt",
                table: "EnrollmentTokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UseCount",
                table: "EnrollmentTokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "EnrollmentTokenId",
                table: "Agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_EnrolledAt",
                table: "Agents",
                column: "EnrolledAt");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_EnrollmentTokenId",
                table: "Agents",
                column: "EnrollmentTokenId");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_EnrollmentTokens_EnrollmentTokenId",
                table: "Agents",
                column: "EnrollmentTokenId",
                principalTable: "EnrollmentTokens",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_EnrollmentTokens_EnrollmentTokenId",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Agents_EnrolledAt",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Agents_EnrollmentTokenId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "EnrollmentTokens");

            migrationBuilder.DropColumn(
                name: "UseCount",
                table: "EnrollmentTokens");

            migrationBuilder.DropColumn(
                name: "EnrollmentTokenId",
                table: "Agents");
        }
    }
}
