using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActorClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActorSuperadministrator = table.Column<bool>(type: "boolean", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ClientIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Sequence",
                table: "AuditEntries",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TenantId_Sequence",
                table: "AuditEntries",
                columns: new[] { "TenantId", "Sequence" });

            // Append-only (#115): the database refuses to change, delete or truncate entries, whoever asks.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'The audit log is append-only.' USING ERRCODE = 'insufficient_privilege';
                END $$;
                CREATE TRIGGER audit_entries_append_only BEFORE UPDATE OR DELETE ON "AuditEntries"
                    FOR EACH ROW EXECUTE FUNCTION audit_entries_append_only();
                CREATE TRIGGER audit_entries_no_truncate BEFORE TRUNCATE ON "AuditEntries"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_entries_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER audit_entries_no_truncate ON "AuditEntries";""");
            migrationBuilder.Sql("""DROP TRIGGER audit_entries_append_only ON "AuditEntries";""");
            migrationBuilder.Sql("DROP FUNCTION audit_entries_append_only();");

            migrationBuilder.DropTable(
                name: "AuditEntries");
        }
    }
}
