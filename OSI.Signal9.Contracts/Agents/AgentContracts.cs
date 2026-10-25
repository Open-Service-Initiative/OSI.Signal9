using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Agents;

/// <summary>
/// One enrolled installation of the agent software on a device, holding its own credential. A machine runs one agent
/// instance per deployment and root tenant tree; each is a separate agent.
/// </summary>
/// <param name="Url">The agent's canonical URL.</param>
/// <param name="EnrollmentTokenId">The enrollment token it enrolled with, while that token exists.</param>
/// <param name="IsActive">Whether this is its device's active agent. A replaced agent is no longer active.</param>
public sealed record AgentDto(
    Guid Id,
    string Url,
    Guid DeviceId,
    Guid TenantId,
    Guid? EnrollmentTokenId,
    string? AgentVersion,
    bool IsActive,
    DateTimeOffset EnrolledAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Sent by a new agent to enroll (POST /api/v1/agents), with no access token: the enrollment token is its authority
/// (stories 95, 98). <see cref="Certificate"/> is the self-signed certificate of the agent's new non-exportable key,
/// which it then authenticates with at the mTLS token endpoint (<c>self_signed_tls_client_auth</c>, RFC 8705 §2.2). An
/// enrollment that matches no existing device creates a new one; devices are never matched by machine name.
/// </summary>
public sealed record EnrollAgentRequest
{
    /// <summary>The enrollment token's secret.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public string EnrollmentToken { get; init; } = string.Empty;

    /// <summary>
    /// The agent's self-signed client certificate, PEM-encoded: an ECDSA P-256 (or stronger) or RSA 3072+ key, the
    /// digitalSignature key usage and clientAuth extended key usage, not a CA, valid now and for at most 398 days.
    /// </summary>
    [Required, StringLength(16384, MinimumLength = 1)]
    public string Certificate { get; init; } = string.Empty;

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
