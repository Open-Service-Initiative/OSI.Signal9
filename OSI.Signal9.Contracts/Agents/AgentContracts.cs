using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Agents;

/// <summary>
/// Derived by the API: Maintenance when flagged, otherwise Online/Offline from the last heartbeat.
/// </summary>
public enum AgentStatus
{
    Online = 0,
    Offline = 1,
    Maintenance = 2
}

public sealed record AgentDto(
    Guid Id,
    Guid TenantId,
    string MachineName,
    string? Domain,
    string OperatingSystem,
    string? OsVersion,
    string? Architecture,
    string? ProcessorName,
    int ProcessorCores,
    long TotalMemoryMb,
    string? IpAddress,
    string? MacAddress,
    string? AgentVersion,
    string? GroupName,
    IReadOnlyList<string> Tags,
    bool InMaintenance,
    AgentStatus Status,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Sent by an agent to enroll itself (POST /api/agents). Re-sending for the same tenant and
/// machine name updates the existing agent instead of creating a duplicate.
/// </summary>
public sealed record RegisterAgentRequest
{
    [Required, StringLength(32, MinimumLength = 2)]
    public string TenantCode { get; init; } = string.Empty;

    [Required, StringLength(255, MinimumLength = 1)]
    public string MachineName { get; init; } = string.Empty;

    [StringLength(255)]
    public string? Domain { get; init; }

    [Required, StringLength(100)]
    public string OperatingSystem { get; init; } = string.Empty;

    [StringLength(100)]
    public string? OsVersion { get; init; }

    [StringLength(20)]
    public string? Architecture { get; init; }

    [StringLength(255)]
    public string? ProcessorName { get; init; }

    [Range(0, 4096)]
    public int ProcessorCores { get; init; }

    [Range(0, long.MaxValue)]
    public long TotalMemoryMb { get; init; }

    [StringLength(45)]
    public string? IpAddress { get; init; }

    [StringLength(17)]
    public string? MacAddress { get; init; }

    [StringLength(50)]
    public string? AgentVersion { get; init; }
}

/// <summary>
/// Full replacement of the admin-managed fields of an agent (PUT semantics).
/// Hardware and OS facts are owned by the agent and change only through registration.
/// </summary>
public sealed record UpdateAgentRequest
{
    [StringLength(100)]
    public string? GroupName { get; init; }

    [MaxLength(50)]
    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool InMaintenance { get; init; }
}

/// <summary>
/// Body of PUT /api/agents/{agentId}/heartbeat. All fields are optional facts that may have changed.
/// </summary>
public sealed record HeartbeatRequest
{
    [StringLength(45)]
    public string? IpAddress { get; init; }

    [StringLength(50)]
    public string? AgentVersion { get; init; }
}
