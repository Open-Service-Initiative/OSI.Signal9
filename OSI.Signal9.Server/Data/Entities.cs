using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.Server.Data;

// Persistence model. Timestamps are stored as UTC DateTime and exposed as DateTimeOffset in contracts.
// Every resource has a Version: its row version, checked on every update and delete (optimistic concurrency).

public sealed class Tenant
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }
    public Guid? ParentTenantId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public SubscriptionPlan Plan { get; set; }
    public int MaxDevices { get; set; }
    public TenantStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Tenant? ParentTenant { get; set; }
    public List<Device> Devices { get; set; } = [];
}

/// <summary>
/// One row of the tenant closure table: <see cref="AncestorId"/> is <see cref="Depth"/> levels above
/// <see cref="DescendantId"/>, and every tenant is paired with itself at depth 0. Derived from the parent
/// pointers and written only by <see cref="Tenants.TenantTree"/>.
/// </summary>
public sealed class TenantClosure
{
    public Guid AncestorId { get; set; }
    public Guid DescendantId { get; set; }
    public int Depth { get; set; }
}

/// <summary>A managed machine. Owns its telemetry and command history across agent reinstalls.</summary>
public sealed class Device : ITenantOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The device's one active agent, if any. Status comes from its check-ins.</summary>
    public Guid? ActiveAgentId { get; set; }

    public required string MachineName { get; set; }
    public string? Domain { get; set; }
    public required string OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? ProcessorName { get; set; }
    public int ProcessorCores { get; set; }
    public long TotalMemoryMb { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public string? GroupName { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool MaintenanceMode { get; set; }

    /// <summary>
    /// Whether the device's next agent must be approved, whatever its tenant's approval setting: set when its agent is
    /// revoked (#129), so reinstalling from the same enrollment token can't silently undo the revocation.
    /// </summary>
    public bool RequiresAgentApproval { get; set; }
    public DateTime CreatedAt { get; set; }

    public Tenant? Tenant { get; set; }
    public Agent? ActiveAgent { get; set; }
}

/// <summary>One installation of the agent software on a device.</summary>
public sealed class Agent : IDeviceOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }
    public Guid DeviceId { get; set; }

    /// <summary>
    /// The enrollment token it enrolled with, while that token exists. The agent's credential is its own certificate,
    /// registered as its OAuth client (its ID is the client ID), so rotating or revoking the token doesn't affect it.
    /// </summary>
    public Guid? EnrollmentTokenId { get; set; }
    public string? AgentVersion { get; set; }
    public DateTime EnrolledAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>When a person revoked it. A revoked agent is no longer its device's active agent and can't authenticate.</summary>
    public DateTime? RevokedAt { get; set; }

    public Device? Device { get; set; }
}

public sealed class Command : IDeviceOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }
    public Guid DeviceId { get; set; }
    public CommandType Type { get; set; }
    public string? Parameters { get; set; }
    public CommandPriority Priority { get; set; }
    public int TimeoutSeconds { get; set; }
    public CommandStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}

public sealed class TelemetrySample : IDeviceOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime CollectedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public double? CpuPercent { get; set; }
    public long? MemoryUsedMb { get; set; }
    public long? MemoryTotalMb { get; set; }
    public int? ProcessCount { get; set; }
    public long? UptimeSeconds { get; set; }
    public List<DiskUsage> Disks { get; set; } = [];
}

public sealed class DiskUsage
{
    public required string Name { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
}

/// <summary>
/// A person known to Signal9 (#63): owned by exactly one tenant, and linked to their Entra identity by directory
/// (<c>tid</c>) and object (<c>oid</c>). Signing in creates no user; a user exists to hold role assignments.
/// </summary>
public sealed class User : ITenantOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }

    /// <summary>The owning tenant, which governs the user.</summary>
    public Guid TenantId { get; set; }
    public Guid EntraTenantId { get; set; }
    public Guid EntraObjectId { get; set; }
    public required string DisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A named bundle of scopes (ADR-0010). Stored as data; the built-in roles are seeded and can't be changed.</summary>
public sealed class Role
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public bool BuiltIn { get; set; }
    public string[] Scopes { get; set; } = [];
}

/// <summary>Grants one role to one user at one tenant, covering the tenant and all its descendants (ADR-0010).</summary>
public sealed class RoleAssignment : ITenantOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }

    /// <summary>The tenant the role is granted at.</summary>
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User? User { get; set; }
    public Role? Role { get; set; }
}

/// <summary>
/// A revocable secret that lets new agents enroll into its tenant (#127, stories 92-94). Only a SHA-256 hash of the
/// secret is stored: the secret is a 256-bit random value, so a fast hash is enough to make a stolen database useless
/// for enrolling. Rotating replaces the secret; agents already enrolled with it are unaffected either way.
/// </summary>
public sealed class EnrollmentToken : ITenantOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>SHA-256 of the secret's UTF-8 bytes; unique, so enrollment finds the token by it.</summary>
    public required byte[] SecretHash { get; set; }

    /// <summary>How many agents can enroll with it; null for unlimited.</summary>
    public int? MaxUses { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RotatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>How many agents have enrolled with it (story 95).</summary>
    public int UseCount { get; set; }

    /// <summary>When an agent last enrolled with it.</summary>
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// One entry of the append-only audit log (#115). Stored at <see cref="TenantId"/>, the tenant where the event happened,
/// or at no tenant for a deployment-level event. It isn't owned by its tenant, so it outlives it, and it refers to
/// devices, users and roles by ID only: names are resolved when it's read, so erasing a device leaves just its ID.
/// The database refuses to update, delete or truncate entries.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; }

    /// <summary>Assigned by the database as entries are written: the log's order, even within one clock tick.</summary>
    public long Sequence { get; set; }
    public DateTime OccurredAt { get; set; }

    /// <summary>The tenant where the event happened; none for a deployment-level event.</summary>
    public Guid? TenantId { get; set; }
    public required string Action { get; set; }
    public AuditOutcome Outcome { get; set; }

    public AuditActorKind ActorKind { get; set; }

    /// <summary>The access token's subject: a person's <c>{tid}:{oid}</c>, or an application's client ID.</summary>
    public string? ActorSubject { get; set; }
    public string? ActorClientId { get; set; }
    public bool ActorSuperadministrator { get; set; }

    /// <summary>What the event was about, by kind (<c>tenant</c>, <c>command</c>, …) and ID.</summary>
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? RoleId { get; set; }

    /// <summary>A short, code-chosen detail (a status, a reason, a route). Never names, serials or output.</summary>
    public string? Detail { get; set; }
    public string? ClientIp { get; set; }
}

/// <summary>
/// An API application acting as itself (#124): registered at <see cref="TenantId"/> and confined to its subtree, with
/// its own role narrowed to <see cref="Scopes"/>. Its OAuth client, holding its credentials, is the OpenIddict
/// application with <see cref="ClientId"/>.
/// </summary>
public sealed class ApiApplication : ITenantOwned
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }
    public Guid TenantId { get; set; }
    public required string ClientId { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>App-only or delegated (<c>ApiApplicationKinds</c>), fixed at registration.</summary>
    public required string Kind { get; set; }

    /// <summary>An app-only application's own role; null for a delegated one, which acts with the person's.</summary>
    public Guid? RoleId { get; set; }

    /// <summary>The scopes it may use: an app-only application's are within its role's.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>Its RFC 7591 <c>token_endpoint_auth_method</c>, fixed at registration.</summary>
    public required string TokenEndpointAuthMethod { get; set; }

    /// <summary>A delegated application's redirect URIs.</summary>
    public string[] RedirectUris { get; set; } = [];

    /// <summary>Whether a delegated application must use PKCE (S256). Off only with <see cref="RequireNonce"/>.</summary>
    public bool RequirePkce { get; set; } = true;

    /// <summary>Whether a delegated application's authorization requests must carry an OpenID Connect nonce.</summary>
    public bool RequireNonce { get; set; }

    public DateTime CreatedAt { get; set; }

    public Role? Role { get; set; }
}
