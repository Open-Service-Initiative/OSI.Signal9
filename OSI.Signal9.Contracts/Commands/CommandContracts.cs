using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Commands;

/// <summary>
/// What a command does. <see cref="RunScript"/> carries the <c>terminal.execute</c> rules until #82 moves ad-hoc code to
/// script executions; no agent runs it yet.
/// </summary>
public enum CommandType
{
    CollectSystemInfo = 0,
    RunScript = 2
}

public enum CommandStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

/// <summary>A command for a device, run by whichever agent is active on it. It stays in the device's history.</summary>
/// <param name="Url">The command's canonical URL, where agents report its result.</param>
public sealed record CommandDto(
    Guid Id,
    Guid DeviceId,
    string Url,
    CommandType Type,
    string? Parameters,
    int TimeoutSeconds,
    CommandStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Output,
    string? Error);

public sealed record CreateCommandRequest
{
    [Required]
    public CommandType Type { get; init; }

    /// <summary>Command-specific JSON payload, e.g. {"serviceName":"Spooler"}.</summary>
    [StringLength(4000)]
    public string? Parameters { get; init; }

    [Range(1, 3600)]
    public int TimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// Body of PUT /api/v1/devices/{deviceId}/commands/{commandId}/result, sent by the agent when it finishes.
/// </summary>
public sealed record CommandResultRequest
{
    public bool Succeeded { get; init; }

    [StringLength(16_000)]
    public string? Output { get; init; }

    [StringLength(4000)]
    public string? Error { get; init; }
}
