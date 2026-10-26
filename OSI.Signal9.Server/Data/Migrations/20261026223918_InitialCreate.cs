using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OSI.Signal9.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
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

            migrationBuilder.CreateTable(
                name: "DashboardSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProtectedTokens = table.Column<byte[]>(type: "bytea", nullable: false),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocationDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocationAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeletedTenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AncestorIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletedTenants", x => x.Id);
                });

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
                name: "FreshSignIns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReauthenticatedFor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FreshSignIns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "text", nullable: true),
                    ClientType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    DisplayNames = table.Column<string>(type: "text", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "text", nullable: true),
                    Permissions = table.Column<string>(type: "text", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    RedirectUris = table.Column<string>(type: "text", nullable: true),
                    Requirements = table.Column<string>(type: "text", nullable: true),
                    Settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Descriptions = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    DisplayNames = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    Resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "StoredKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Use = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActivatesAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Certificate = table.Column<byte[]>(type: "bytea", nullable: false),
                    KekId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WrappedKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    EncryptedPrivateKey = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    ParentTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContactPhone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MaxDevices = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HoldsNewAgentsForApproval = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tenants_Tenants_ParentTenantId",
                        column: x => x.ParentTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationId = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    Scopes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_Application~",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ApiApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    TokenEndpointAuthMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RedirectUris = table.Column<string[]>(type: "text[]", nullable: false),
                    RequirePkce = table.Column<bool>(type: "boolean", nullable: false),
                    RequireNonce = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiApplications_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApiApplications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    MaxUses = table.Column<int>(type: "integer", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RotatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UseCount = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrollmentTokens_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "TenantTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromParentTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToParentTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProposedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantTransfers_Tenants_FromParentTenantId",
                        column: x => x.FromParentTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TenantTransfers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TenantTransfers_Tenants_ToParentTenantId",
                        column: x => x.ToParentTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "OpenIddictTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationId = table.Column<string>(type: "text", nullable: true),
                    AuthorizationId = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntraTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReferenceHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RedeemedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RedeemedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invitations_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invitations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Invitations_Users_RedeemedByUserId",
                        column: x => x.RedeemedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Invitations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
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

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    EnrollmentTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EnrolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AwaitingApproval = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_EnrollmentTokens_EnrollmentTokenId",
                        column: x => x.EnrollmentTokenId,
                        principalTable: "EnrollmentTokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveAgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    MachineName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OsFamily = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
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
                    RequiresAgentApproval = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SmbiosUuid = table.Column<Guid>(type: "uuid", nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
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
                name: "PendingIdentifications",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OsFamily = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
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

            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Parameters = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
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

            migrationBuilder.CreateTable(
                name: "TelemetrySamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: true),
                    MemoryUsedMb = table.Column<long>(type: "bigint", nullable: true),
                    MemoryTotalMb = table.Column<long>(type: "bigint", nullable: true),
                    ProcessCount = table.Column<int>(type: "integer", nullable: true),
                    UptimeSeconds = table.Column<long>(type: "bigint", nullable: true),
                    Disks = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetrySamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelemetrySamples_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
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
                name: "IX_Agents_DeviceId",
                table: "Agents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_EnrolledAt",
                table: "Agents",
                column: "EnrolledAt");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_EnrollmentTokenId",
                table: "Agents",
                column: "EnrollmentTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiApplications_ClientId",
                table: "ApiApplications",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiApplications_RoleId",
                table: "ApiApplications",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiApplications_TenantId",
                table: "ApiApplications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Sequence",
                table: "AuditEntries",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TenantId_Sequence",
                table: "AuditEntries",
                columns: new[] { "TenantId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_DeviceId_Status",
                table: "Commands",
                columns: new[] { "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardSessions_RevocationDueAt",
                table: "DashboardSessions",
                column: "RevocationDueAt",
                filter: "\"EndedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardSessions_Subject",
                table: "DashboardSessions",
                column: "Subject");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ActiveAgentId",
                table: "Devices",
                column: "ActiveAgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SerialNumber_Manufacturer",
                table: "Devices",
                columns: new[] { "SerialNumber", "Manufacturer" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SmbiosUuid",
                table: "Devices",
                column: "SmbiosUuid");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId_MachineName",
                table: "Devices",
                columns: new[] { "TenantId", "MachineName" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_SecretHash",
                table: "EnrollmentTokens",
                column: "SecretHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_TenantId",
                table: "EnrollmentTokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FreshSignIns_ExpiresAt",
                table: "FreshSignIns",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_RedeemedByUserId",
                table: "Invitations",
                column: "RedeemedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_ReferenceHash",
                table: "Invitations",
                column: "ReferenceHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_RoleId",
                table: "Invitations",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TenantId",
                table: "Invitations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_UserId",
                table: "Invitations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictApplications_ClientId",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingIdentifications_TenantId",
                table: "PendingIdentifications",
                column: "TenantId");

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

            migrationBuilder.CreateIndex(
                name: "IX_StoredKeys_Use_ActivatesAt",
                table: "StoredKeys",
                columns: new[] { "Use", "ActivatesAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySamples_DeviceId_CollectedAt",
                table: "TelemetrySamples",
                columns: new[] { "DeviceId", "CollectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantClosures_DescendantId",
                table: "TenantClosures",
                column: "DescendantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_ParentTenantId",
                table: "Tenants",
                column: "ParentTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantTransfers_FromParentTenantId",
                table: "TenantTransfers",
                column: "FromParentTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantTransfers_TenantId",
                table: "TenantTransfers",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_TenantTransfers_ToParentTenantId",
                table: "TenantTransfers",
                column: "ToParentTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EntraTenantId_EntraObjectId",
                table: "Users",
                columns: new[] { "EntraTenantId", "EntraObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                table: "Users",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Devices_DeviceId",
                table: "Agents",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

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

            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Devices_DeviceId",
                table: "Agents");

            migrationBuilder.DropTable(
                name: "ApiApplications");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "Commands");

            migrationBuilder.DropTable(
                name: "DashboardSessions");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "DeletedTenants");

            migrationBuilder.DropTable(
                name: "EndedEntraSessions");

            migrationBuilder.DropTable(
                name: "EntraTokenCache");

            migrationBuilder.DropTable(
                name: "FreshSignIns");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "OpenIddictScopes");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens");

            migrationBuilder.DropTable(
                name: "PendingIdentifications");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "SignInSessions");

            migrationBuilder.DropTable(
                name: "StoredKeys");

            migrationBuilder.DropTable(
                name: "TelemetrySamples");

            migrationBuilder.DropTable(
                name: "TenantClosures");

            migrationBuilder.DropTable(
                name: "TenantTransfers");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "EnrollmentTokens");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
