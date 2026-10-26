using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
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

    /// <summary>
    /// Whether this tenant holds agents newly enrolling into it or its descendants for approval (story 96). Descendants
    /// inherit it and can't turn it off; their own value says only whether it's also set there.
    /// </summary>
    public bool HoldsNewAgentsForApproval { get; set; }
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

/// <summary>
/// The anonymous record a deleted tenant leaves (#134, story 13): only its ID and the ancestors it had when it was
/// deleted, nearest first. Its audit trail stays readable, with <c>audit.read</c>, at those ancestors, whatever becomes
/// of the tree afterwards.
/// </summary>
public sealed class DeletedTenant
{
    public Guid Id { get; set; }
    public Guid[] AncestorIds { get; set; } = [];
    public DateTime DeletedAt { get; set; }
}

/// <summary>
/// A tenant transfer (#122, ADR-0001): a proposal to move <see cref="TenantId"/>, with its descendants, under
/// <see cref="ToParentTenantId"/>. Both sides see it: people who reach the tenant, its parent as it was when proposed,
/// or the new parent. Accepting it moves the tenant in the same transaction.
/// </summary>
public sealed class TenantTransfer
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }

    /// <summary>The tenant being transferred, which owns the transfer.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Its parent when proposed: none for a root, or once that tenant is deleted.</summary>
    public Guid? FromParentTenantId { get; set; }
    public Guid ToParentTenantId { get; set; }
    public TenantTransferStatus Status { get; set; }
    public DateTime ProposedAt { get; set; }

    /// <summary>When it was accepted (and so completed), declined or cancelled.</summary>
    public DateTime? DecidedAt { get; set; }
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
    public OsFamily OsFamily { get; set; }
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

    /// <summary>
    /// The SMBIOS system UUID its latest agent reported, unless it was zeroed or a known placeholder. With
    /// <see cref="Manufacturer"/> and <see cref="SerialNumber"/>, what identifies a reinstalled agent's device (#131).
    /// </summary>
    public Guid? SmbiosUuid { get; set; }

    /// <summary>The system manufacturer its latest agent reported, normalized (trimmed, upper case), unless junk.</summary>
    public string? Manufacturer { get; set; }

    /// <summary>The system serial number its latest agent reported, normalized (trimmed, upper case), unless junk.</summary>
    public string? SerialNumber { get; set; }

    public Tenant? Tenant { get; set; }
    public Agent? ActiveAgent { get; set; }
}

/// <summary>
/// One installation of the agent software. It belongs to a device, except while it awaits an identification decision
/// (<see cref="PendingIdentification"/>).
/// </summary>
public sealed class Agent
{
    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the resource's ETag.</summary>
    public uint Version { get; set; }

    /// <summary>Its device, or none while it awaits an identification decision.</summary>
    public Guid? DeviceId { get; set; }

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

    /// <summary>
    /// Whether it waits for a person's approval before it receives work: it enrolled under an approval hold, or onto a
    /// device that required approval (story 97). Cleared when it's approved.
    /// </summary>
    public bool AwaitingApproval { get; set; }

    public Device? Device { get; set; }

    /// <summary>What it reported at enrollment, while a person decides which device it belongs to.</summary>
    public PendingIdentification? PendingIdentification { get; set; }
}

/// <summary>
/// An agent in the Needs-decision queue (story 105): its hardware matched devices it couldn't be matched to
/// automatically, so it waits, with no device, until a person decides it's a reinstall of one of them or a new device.
/// Also a held fresh-OS claim's: it has joined its device, but waits for approval before it replaces the device's
/// agent. Holds what the agent reported at enrollment, to create or update its device from. Removed once decided or
/// approved.
/// </summary>
public sealed class PendingIdentification : ITenantOwned
{
    public Guid AgentId { get; set; }

    /// <summary>The enrollment token's tenant: whose root tree its device is looked for in, and where a new one goes.</summary>
    public Guid TenantId { get; set; }
    public required string MachineName { get; set; }
    public string? Domain { get; set; }
    public OsFamily OsFamily { get; set; }
    public required string OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? ProcessorName { get; set; }
    public int ProcessorCores { get; set; }
    public long TotalMemoryMb { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }

    /// <summary>The normalized hardware identifiers, as <see cref="Device"/> keeps them.</summary>
    public Guid? SmbiosUuid { get; set; }
    public string? Manufacturer { get; set; }
    public string? SerialNumber { get; set; }
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
/// A single-use offer of one role assignment, <see cref="RoleId"/> at <see cref="TenantId"/> (#121, stories 28-37),
/// redeemed at auth after Entra sign-in. Its link carries only an opaque 256-bit reference, of which only a SHA-256 hash
/// is stored, so a link can't be forged or altered and a stolen database redeems nothing. It's never matched by email:
/// only <see cref="UserId"/> and <see cref="EntraTenantId"/> narrow who can redeem it.
/// </summary>
public sealed class Invitation : ITenantOwned
{
    /// <summary>How long an invitation can be redeemed for (story 32).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    public Guid Id { get; set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>): the concurrency token behind the ETag, and what makes it single use.</summary>
    public uint Version { get; set; }

    /// <summary>The tenant the role is offered at.</summary>
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }

    /// <summary>The existing user it's for, if any: only their Entra identity can redeem it (story 35).</summary>
    public Guid? UserId { get; set; }

    /// <summary>The Entra directory (<c>tid</c>) it's pinned to, if any (story 33).</summary>
    public Guid? EntraTenantId { get; set; }

    /// <summary>SHA-256 of the reference's UTF-8 bytes; unique, so redemption finds the invitation by it.</summary>
    public required byte[] ReferenceHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }

    /// <summary>The user who redeemed it; cleared if that user is later deleted.</summary>
    public Guid? RedeemedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
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
