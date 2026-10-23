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
    public string? AgentVersion { get; set; }
    public DateTime EnrolledAt { get; set; }
    public DateTime LastSeenAt { get; set; }

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
