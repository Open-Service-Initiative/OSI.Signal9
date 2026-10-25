using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Devices;

/// <summary>
/// Online while the device's active agent keeps checking in. A device with no active agent is Offline.
/// </summary>
public enum DeviceStatus
{
    Online = 0,
    Offline = 1
}

/// <summary>
/// A managed machine. It keeps its telemetry and command history when its agent is replaced.
/// </summary>
/// <param name="ActiveAgentId">The device's one active agent, if it has one.</param>
/// <param name="LastSeenAt">When the active agent last checked in, if there is one.</param>
/// <param name="RequiresAgentApproval">
/// Whether the device's next agent must be approved, whatever its tenant's approval setting: set when its agent was
/// revoked, so reinstalling from the same enrollment token can't silently undo the revocation.
/// </param>
/// <param name="AgentStatus">
/// Whether the active agent may receive commands, if there is one: separate from <see cref="Status"/>, which says only
/// whether it's checking in.
/// </param>
public sealed record DeviceDto(
    Guid Id,
    Guid TenantId,
    Guid? ActiveAgentId,
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
    string? GroupName,
    IReadOnlyList<string> Tags,
    bool MaintenanceMode,
    DeviceStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool RequiresAgentApproval,
    Agents.AgentStatus? AgentStatus);

/// <summary>
/// The fields of a device a person can edit, sent as a JSON merge patch (RFC 7396) to PATCH /api/v1/devices/{deviceId}.
/// Fields left out of the patch keep their current values.
/// </summary>
public sealed record UpdateDeviceRequest
{
    [StringLength(100)]
    public string? GroupName { get; init; }

    [MaxLength(50)]
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Planned downtime: the device raises no alerts while it is on.</summary>
    public bool MaintenanceMode { get; init; }
}
