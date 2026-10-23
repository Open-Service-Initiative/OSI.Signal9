using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Commands;

public enum CommandType
{
    CollectSystemInfo = 0,
    RestartService = 1,
    RunScript = 2,
    RestartMachine = 3
}

public enum CommandStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

public enum CommandPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public sealed record AgentCommandDto(
    Guid Id,
    Guid AgentId,
    CommandType Type,
    string? Parameters,
    CommandPriority Priority,
    int TimeoutSeconds,
    CommandStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Output,
    string? Error);

public sealed record CreateAgentCommandRequest
{
    [Required]
    public CommandType Type { get; init; }

    /// <summary>Command-specific JSON payload, e.g. {"serviceName":"Spooler"}.</summary>
    [StringLength(4000)]
    public string? Parameters { get; init; }

    public CommandPriority Priority { get; init; } = CommandPriority.Normal;

    [Range(1, 3600)]
    public int TimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// Body of PUT /api/agents/{agentId}/commands/{commandId}/result, sent by the agent when it finishes.
/// </summary>
public sealed record CommandResultRequest
{
    public bool Succeeded { get; init; }

    [StringLength(16_000)]
    public string? Output { get; init; }

    [StringLength(4000)]
    public string? Error { get; init; }
}
