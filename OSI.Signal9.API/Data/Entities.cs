using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Data;

// Persistence model. Timestamps are stored as UTC DateTime and exposed as DateTimeOffset in contracts.

public sealed class Tenant
{
    public Guid Id { get; set; }
    public Guid? ParentTenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public SubscriptionPlan Plan { get; set; }
    public int MaxAgents { get; set; }
    public TenantStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Tenant? ParentTenant { get; set; }
    public List<Agent> Agents { get; set; } = [];
}

public sealed class Agent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
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
    public string? AgentVersion { get; set; }
    public string? GroupName { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool InMaintenance { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public Tenant? Tenant { get; set; }
}

public sealed class AgentCommand
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
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

public sealed class TelemetrySample
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
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
