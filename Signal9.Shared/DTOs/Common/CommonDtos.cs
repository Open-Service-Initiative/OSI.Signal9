using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.DTOs.Core;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs.Common;

/// <summary>
/// Modern Agent Response DTO - Clean Entity Framework integration
/// </summary>
public record AgentResponse : BaseDto<Guid>
{
    /// <summary>
    /// The agent data
    /// </summary>
    public AgentDto? Agent { get; init; }
    
    /// <summary>
    /// Tenant name for display
    /// </summary>
    public string? TenantName { get; init; }
    
    /// <summary>
    /// Group name for organization
    /// </summary>
    public string? GroupName { get; init; }
    
    /// <summary>
    /// Whether the agent is online (computed from status)
    /// </summary>
    public bool IsOnline => Agent?.Status == AgentStatus.Online;
    
    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Factory method from AgentDto
    /// </summary>
    public static AgentResponse FromAgentDto(AgentDto agent, string? tenantName = null, string? groupName = null)
    {
        return new AgentResponse
        {
            Id = Guid.NewGuid(),
            Agent = agent,
            TenantName = tenantName,
            GroupName = groupName
        };
    }
}

/// <summary>
/// Modern Command Request DTO - Clean API contract
/// </summary>
public record CommandRequest : BaseDto<Guid>
{
    [Required(ErrorMessage = "AgentId is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "AgentId must be between 1 and 50 characters")]
    public required Guid AgentId { get; init; }

    public required CommandType CommandType { get; init; }

    public string? Parameters { get; init; }

    public CommandPriority Priority { get; init; } = CommandPriority.Normal;

    [Range(1, int.MaxValue, ErrorMessage = "TimeoutSeconds must be at least 1")]
    public int TimeoutSeconds { get; init; } = 300;

    [StringLength(100, ErrorMessage = "InitiatedBy cannot exceed 100 characters")]
    public string? InitiatedBy { get; init; }

    /// <summary>
    /// Convert to AgentCommandDto
    /// </summary>
    public AgentCommandDto ToAgentCommandDto()
    {
        return new AgentCommandDto
        {
            AgentId = AgentId,
            CommandType = CommandType,
            Parameters = Parameters,
            Status = CommandStatus.Pending,
            ScheduledAt = DateTime.UtcNow,
            StartedAt = null,
            CompletedAt = null,
            Result = null,
            ErrorMessage = null,
            InitiatedBy = InitiatedBy,
            TimeoutSeconds = TimeoutSeconds,
            Priority = Priority
        };
    }
}

/// <summary>
/// Modern Command Response DTO - Clean API contract
/// </summary>
public record CommandResponse : BaseDto<Guid>
{
    public AgentCommandDto? Command { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? Message { get; init; }

    /// <summary>
    /// Factory method from AgentCommandDto
    /// </summary>
    public static CommandResponse FromCommandDto(AgentCommandDto command, bool isSuccess = true, string? message = null)
    {
        return new CommandResponse
        {
            Id = Guid.NewGuid(),
            Command = command,
            IsSuccess = isSuccess,
            Message = message
        };
    }
}

/// <summary>
/// Modern Telemetry Response DTO - Clean API contract
/// </summary>
public record TelemetryResponse : BaseDto<Guid>
{
    public TelemetryDataDto? Telemetry { get; init; }
    public Dictionary<string, double> ComputedMetrics { get; init; } = [];
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Factory method from TelemetryDataDto
    /// </summary>
    public static TelemetryResponse FromTelemetryDto(TelemetryDataDto telemetry, Dictionary<string, double>? computedMetrics = null)
    {
        return new TelemetryResponse
        {
            Id = Guid.NewGuid(),
            Telemetry = telemetry,
            ComputedMetrics = computedMetrics ?? [],
            CollectedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Modern System Information DTO - Clean agent data collection
/// Follows C# naming conventions and modern patterns
/// </summary>
public record SystemInfo
{
    public string MachineName { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string? OsVersion { get; init; }
    public string? Architecture { get; init; }
    public string? IpAddress { get; init; }
    public string? MacAddress { get; init; }
    public long TotalMemoryMb { get; init; }
    public int ProcessorCores { get; init; }
    public string? ProcessorName { get; init; }
    public string? Domain { get; init; }
    
    // Compatibility properties for existing SystemInfoProvider
    public int ProcessorCount => ProcessorCores;
    public string? UserName { get; init; }
    public long TotalMemory => TotalMemoryMb * 1024 * 1024; // Convert MB to bytes
    public long AvailableMemory { get; init; }
    public TimeSpan Uptime { get; init; }
    public Dictionary<string, object> Drives { get; init; } = [];
}