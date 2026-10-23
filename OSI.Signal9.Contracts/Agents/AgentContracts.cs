using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Agents;

/// <summary>
/// One installation of the agent software on a device.
/// </summary>
/// <param name="IsActive">Whether this is its device's active agent. A replaced agent is no longer active.</param>
public sealed record AgentDto(
    Guid Id,
    Guid DeviceId,
    Guid TenantId,
    string? AgentVersion,
    bool IsActive,
    DateTimeOffset EnrolledAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Sent by an agent to register itself (POST /api/v1/agents). Each registration is a new agent, which becomes the
/// active agent of the tenant's device with the same machine name, or of a new device.
/// </summary>
// TODO(enrollment): replaced wholesale by enrollment tokens (#128) and identification (#131), which never match machine names.
public sealed record RegisterAgentRequest
{
    /// <summary>The tenant to enroll in, until enrollment tokens replace it.</summary>
    [Required]
    public Guid TenantId { get; init; }

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
/// Body of PUT /api/v1/agents/{agentId}/heartbeat. All fields are optional facts that may have changed.
/// </summary>
public sealed record HeartbeatRequest
{
    [StringLength(45)]
    public string? IpAddress { get; init; }

    [StringLength(50)]
    public string? AgentVersion { get; init; }
}

/// <summary>Response to a heartbeat.</summary>
/// <param name="HasPendingWork">Whether work is waiting for the agent, which it then fetches from its pending-commands list.</param>
public sealed record HeartbeatResponse(bool HasPendingWork);
