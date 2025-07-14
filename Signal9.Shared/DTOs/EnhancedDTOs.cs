using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.Models;
using Signal9.Shared.Contracts;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Enhanced Agent DTO that leverages shared contracts to eliminate property duplication
/// Implements IAgentData for consistent interface across entities and DTOs
/// </summary>
public record AgentDto : TenantScopedDto, IAgentData
{
    /// <summary>
    /// Machine name of the agent
    /// </summary>
    [Required(ErrorMessage = "MachineName is required")]
    [StringLength(255, MinimumLength = 1, ErrorMessage = "MachineName must be between 1 and 255 characters")]
    public required string MachineName { get; init; }
    
    /// <summary>
    /// Domain the machine belongs to
    /// </summary>
    [StringLength(255, ErrorMessage = "Domain cannot exceed 255 characters")]
    public string? Domain { get; init; }
    
    /// <summary>
    /// Operating system type
    /// </summary>
    [Required(ErrorMessage = "OperatingSystem is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "OperatingSystem must be between 1 and 50 characters")]
    public required string OperatingSystem { get; init; }
    
    /// <summary>
    /// Operating system version
    /// </summary>
    [StringLength(50, ErrorMessage = "OSVersion cannot exceed 50 characters")]
    public string? OSVersion { get; init; }
    
    /// <summary>
    /// System architecture (x64, x86, ARM64)
    /// </summary>
    [StringLength(20, ErrorMessage = "Architecture cannot exceed 20 characters")]
    public string? Architecture { get; init; }
    
    /// <summary>
    /// Total system memory in MB
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "TotalMemoryMB must be a non-negative value")]
    public long TotalMemoryMB { get; init; }
    
    /// <summary>
    /// Number of processor cores
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ProcessorCores must be at least 1")]
    public int ProcessorCores { get; init; }
    
    /// <summary>
    /// Processor name/model
    /// </summary>
    [StringLength(500, ErrorMessage = "ProcessorName cannot exceed 500 characters")]
    public string? ProcessorName { get; init; }
    
    /// <summary>
    /// Primary IP address
    /// </summary>
    [Required(ErrorMessage = "IpAddress is required")]
    [StringLength(45, MinimumLength = 7, ErrorMessage = "IpAddress must be between 7 and 45 characters")]
    [RegularExpression(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$|^(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}$", 
        ErrorMessage = "IpAddress must be a valid IPv4 or IPv6 address")]
    public required string IpAddress { get; init; }
    
    /// <summary>
    /// Primary MAC address
    /// </summary>
    [StringLength(17, ErrorMessage = "MacAddress cannot exceed 17 characters")]
    [RegularExpression(@"^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$", 
        ErrorMessage = "MacAddress must be in the format XX:XX:XX:XX:XX:XX or XX-XX-XX-XX-XX-XX")]
    public string? MacAddress { get; init; }
    
    /// <summary>
    /// When the agent was first seen
    /// </summary>
    public DateTime FirstSeen { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Last time the agent checked in
    /// </summary>
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Current agent status
    /// </summary>
    public AgentStatus Status { get; init; } = AgentStatus.Online;
    
    /// <summary>
    /// Agent version
    /// </summary>
    [StringLength(50, ErrorMessage = "Version cannot exceed 50 characters")]
    public string? Version { get; init; }
    
    /// <summary>
    /// Tags for organization and filtering
    /// </summary>
    [StringLength(1000, ErrorMessage = "Tags cannot exceed 1000 characters")]
    public string? Tags { get; init; }
    
    /// <summary>
    /// Computed property to check if agent is online
    /// </summary>
    public bool IsOnline => Status == AgentStatus.Online && LastSeen > DateTime.UtcNow.AddMinutes(-5);
    
    /// <summary>
    /// Group name for UI display (derived from Tags or defaults)
    /// </summary>
    public string? GroupName => string.IsNullOrWhiteSpace(Tags) ? "Default" : Tags;
}

/// <summary>
/// Telemetry data DTO that leverages shared contracts
/// Implements ITelemetryData for consistent interface across entities and DTOs
/// </summary>
public record TelemetryDataDto : TenantScopedDto, ITelemetryData
{
    /// <summary>
    /// Agent ID that generated this telemetry
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "AgentId must be between 1 and 50 characters")]
    public required string AgentId { get; init; }
    
    /// <summary>
    /// Type of telemetry data
    /// </summary>
    public required TelemetryType TelemetryType { get; init; }
    
    /// <summary>
    /// CPU usage percentage (0-100)
    /// </summary>
    [Range(0, 100, ErrorMessage = "CpuUsagePercent must be between 0 and 100")]
    public double? CpuUsagePercent { get; init; }
    
    /// <summary>
    /// Memory usage in MB
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "MemoryUsageMB must be non-negative")]
    public long? MemoryUsageMB { get; init; }
    
    /// <summary>
    /// Available memory in MB
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "AvailableMemoryMB must be non-negative")]
    public long? AvailableMemoryMB { get; init; }
    
    /// <summary>
    /// Disk usage information (JSON serialized)
    /// </summary>
    public string? DiskUsage { get; init; }
    
    /// <summary>
    /// Network interfaces information (JSON serialized)
    /// </summary>
    public string? NetworkInterfaces { get; init; }
    
    /// <summary>
    /// Running processes count
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "ProcessCount must be non-negative")]
    public int? ProcessCount { get; init; }
    
    /// <summary>
    /// System uptime in seconds
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "UptimeSeconds must be non-negative")]
    public long? UptimeSeconds { get; init; }
    
    /// <summary>
    /// System load average (Linux/Mac)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "LoadAverage must be non-negative")]
    public double? LoadAverage { get; init; }
    
    /// <summary>
    /// Temperature readings (JSON serialized)
    /// </summary>
    public string? TemperatureReadings { get; init; }
    
    /// <summary>
    /// Custom metrics (JSON serialized)
    /// </summary>
    public string? CustomMetrics { get; init; }
    
    /// <summary>
    /// Error message if telemetry collection failed
    /// </summary>
    [StringLength(1000, ErrorMessage = "ErrorMessage cannot exceed 1000 characters")]
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Agent command DTO that leverages shared contracts
/// Implements IAgentCommandData for consistent interface across entities and DTOs
/// </summary>
public record AgentCommandDto : TenantScopedDto, IAgentCommandData
{
    /// <summary>
    /// Target agent ID
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "AgentId must be between 1 and 50 characters")]
    public required string AgentId { get; init; }
    
    /// <summary>
    /// Type of command to execute
    /// </summary>
    public required CommandType CommandType { get; init; }
    
    /// <summary>
    /// Command parameters (JSON serialized)
    /// </summary>
    public string? Parameters { get; init; }
    
    /// <summary>
    /// Current execution status
    /// </summary>
    public CommandStatus Status { get; init; } = CommandStatus.Pending;
    
    /// <summary>
    /// When the command was scheduled
    /// </summary>
    public DateTime ScheduledAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the command execution started
    /// </summary>
    public DateTime? StartedAt { get; init; }
    
    /// <summary>
    /// When the command execution completed
    /// </summary>
    public DateTime? CompletedAt { get; init; }
    
    /// <summary>
    /// Command execution result
    /// </summary>
    public string? Result { get; init; }
    
    /// <summary>
    /// Error message if command failed
    /// </summary>
    [StringLength(1000, ErrorMessage = "ErrorMessage cannot exceed 1000 characters")]
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// User who initiated the command
    /// </summary>
    [StringLength(100, ErrorMessage = "InitiatedBy cannot exceed 100 characters")]
    public string? InitiatedBy { get; init; }
    
    /// <summary>
    /// Command timeout in seconds
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "TimeoutSeconds must be at least 1")]
    public int TimeoutSeconds { get; init; } = 300;
    
    /// <summary>
    /// Command priority
    /// </summary>
    public CommandPriority Priority { get; init; } = CommandPriority.Normal;
}

/// <summary>
/// Tenant DTO that leverages shared contracts
/// Implements ITenantData for consistent interface across entities and DTOs
/// </summary>
public record TenantDto : BaseDto, ITenantData
{
    /// <summary>
    /// Unique tenant code for registration
    /// </summary>
    [Required(ErrorMessage = "TenantCode is required")]
    [StringLength(20, MinimumLength = 1, ErrorMessage = "TenantCode must be between 1 and 20 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\-_]+$", ErrorMessage = "TenantCode can only contain alphanumeric characters, hyphens, and underscores")]
    public required string TenantCode { get; init; }
    
    /// <summary>
    /// Tenant display name
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 200 characters")]
    public required string Name { get; init; }
    
    /// <summary>
    /// Tenant description
    /// </summary>
    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }
    
    /// <summary>
    /// Primary contact email
    /// </summary>
    [Required(ErrorMessage = "ContactEmail is required")]
    [StringLength(255, ErrorMessage = "ContactEmail cannot exceed 255 characters")]
    [EmailAddress(ErrorMessage = "ContactEmail must be a valid email address")]
    public required string ContactEmail { get; init; }
    
    /// <summary>
    /// Tenant subscription tier
    /// </summary>
    public SubscriptionTier SubscriptionTier { get; init; } = SubscriptionTier.Basic;
    
    /// <summary>
    /// Maximum number of agents allowed
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxAgents must be at least 1")]
    public int MaxAgents { get; init; } = 10;
    
    /// <summary>
    /// Data retention period in days
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "DataRetentionDays must be at least 1")]
    public int DataRetentionDays { get; init; } = 30;
    
    /// <summary>
    /// Tenant configuration settings (JSON)
    /// </summary>
    public string? Settings { get; init; }
    
    /// <summary>
    /// Tenant status
    /// </summary>
    public TenantStatus Status { get; init; } = TenantStatus.Active;
    
    /// <summary>
    /// Subscription expiration date
    /// </summary>
    public DateTime? SubscriptionExpiresAt { get; init; }
    
    /// <summary>
    /// API key for tenant authentication (excluded from public responses)
    /// </summary>
    public string? ApiKey { get; init; }
    
    /// <summary>
    /// Tenant ID for multi-tenant scenarios (same as Id for tenant entities)
    /// </summary>
    public Guid TenantId => Guid.TryParse(Id, out var guid) ? guid : Guid.Empty;
    
    /// <summary>
    /// When the tenant was created
    /// </summary>
    public DateTime CreatedAt => Timestamp;
    
    /// <summary>
    /// When the tenant was last updated (defaults to creation time)
    /// </summary>
    public DateTime UpdatedAt => Timestamp;
}

/// <summary>
/// Request DTO for querying agents with filtering and pagination
/// </summary>
public record AgentQueryRequest : TenantScopedDto
{
    /// <summary>
    /// Filter by agent status
    /// </summary>
    public AgentStatus? Status { get; init; }

    /// <summary>
    /// Filter by platform (Windows, Linux, macOS)
    /// </summary>
    [StringLength(50, ErrorMessage = "Platform cannot exceed 50 characters")]
    public string? Platform { get; init; }

    /// <summary>
    /// Search term for machine name, IP address, or description
    /// </summary>
    [StringLength(100, ErrorMessage = "Search term cannot exceed 100 characters")]
    public string? Search { get; init; }

    /// <summary>
    /// Filter by online status
    /// </summary>
    public bool? IsOnline { get; init; }

    /// <summary>
    /// Page number for pagination (1-based)
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Page must be greater than 0")]
    public int Page { get; init; } = 1;

    /// <summary>
    /// Number of items per page
    /// </summary>
    [Range(1, 100, ErrorMessage = "PageSize must be between 1 and 100")]
    public int PageSize { get; init; } = 20;

    /// <summary>
    /// Field to sort by
    /// </summary>
    [StringLength(50, ErrorMessage = "SortBy cannot exceed 50 characters")]
    public string SortBy { get; init; } = "MachineName";

    /// <summary>
    /// Sort order (asc/desc)
    /// </summary>
    [StringLength(10, ErrorMessage = "SortOrder cannot exceed 10 characters")]
    public string SortOrder { get; init; } = "asc";
}

/// <summary>
/// Request DTO for updating agent information
/// </summary>
public record AgentUpdateRequest : TenantScopedDto
{
    /// <summary>
    /// Updated machine name
    /// </summary>
    [StringLength(255, ErrorMessage = "MachineName cannot exceed 255 characters")]
    public string? MachineName { get; init; }

    /// <summary>
    /// Updated description
    /// </summary>
    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }

    /// <summary>
    /// Updated tags
    /// </summary>
    [StringLength(1000, ErrorMessage = "Tags cannot exceed 1000 characters")]
    public string? Tags { get; init; }

    /// <summary>
    /// Updated status
    /// </summary>
    public AgentStatus? Status { get; init; }
}

/// <summary>
/// Request DTO for creating a new agent
/// </summary>
public record CreateAgentRequest : TenantScopedDto
{
    /// <summary>
    /// Machine name of the agent
    /// </summary>
    [Required(ErrorMessage = "MachineName is required")]
    [StringLength(255, MinimumLength = 1, ErrorMessage = "MachineName must be between 1 and 255 characters")]
    public required string MachineName { get; init; }
    
    /// <summary>
    /// Operating system type
    /// </summary>
    [Required(ErrorMessage = "OperatingSystem is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "OperatingSystem must be between 1 and 50 characters")]
    public required string OperatingSystem { get; init; }
    
    /// <summary>
    /// Primary IP address
    /// </summary>
    [Required(ErrorMessage = "IpAddress is required")]
    [StringLength(45, MinimumLength = 7, ErrorMessage = "IpAddress must be between 7 and 45 characters")]
    public required string IpAddress { get; init; }
    
    /// <summary>
    /// Agent description
    /// </summary>
    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }
    
    /// <summary>
    /// Tags for organization
    /// </summary>
    [StringLength(1000, ErrorMessage = "Tags cannot exceed 1000 characters")]
    public string? Tags { get; init; }
}

/// <summary>
/// Response DTO for agent operations with enriched data
/// </summary>
public record AgentResponse : TenantScopedDto
{
    /// <summary>
    /// The core agent data
    /// </summary>
    [Required]
    public required AgentDto Agent { get; init; }

    /// <summary>
    /// Tenant name for display purposes
    /// </summary>
    public string? TenantName { get; init; }

    /// <summary>
    /// Agent group name if applicable
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>
    /// Computed online status
    /// </summary>
    public bool IsOnline => Agent.Status == AgentStatus.Online;

    /// <summary>
    /// Time since last seen in human-readable format
    /// </summary>
    public string LastSeenDisplay => Agent.LastSeen.ToString("yyyy-MM-dd HH:mm:ss UTC");

    /// <summary>
    /// Create an agent response from agent DTO
    /// </summary>
    public static AgentResponse Create(AgentDto agent, string? tenantName = null, string? groupName = null)
    {
        return new AgentResponse
        {
            TenantId = agent.TenantId,
            Agent = agent,
            TenantName = tenantName,
            GroupName = groupName
        };
    }
}

/// <summary>
/// Request DTO for agent registration in the Signal9 RMM system
/// </summary>
public record AgentRegistrationRequest : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier of the agent
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "AgentId must be between 1 and 100 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\-_]+$", ErrorMessage = "AgentId can only contain alphanumeric characters, hyphens, and underscores")]
    public required string AgentId { get; init; }

    /// <summary>
    /// Gets the tenant code for authentication purposes
    /// </summary>
    [Required(ErrorMessage = "TenantCode is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "TenantCode must be between 1 and 50 characters")]
    public required string TenantCode { get; init; }

    /// <summary>
    /// Gets the name of the machine where the agent is installed
    /// </summary>
    [Required(ErrorMessage = "MachineName is required")]
    [StringLength(255, MinimumLength = 1, ErrorMessage = "MachineName must be between 1 and 255 characters")]
    public required string MachineName { get; init; }

    /// <summary>
    /// Gets the operating system of the host machine
    /// </summary>
    [Required(ErrorMessage = "OperatingSystem is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "OperatingSystem must be between 1 and 50 characters")]
    public required string OperatingSystem { get; init; }

    /// <summary>
    /// Gets the operating system version
    /// </summary>
    [StringLength(50, ErrorMessage = "OSVersion cannot exceed 50 characters")]
    public string? OSVersion { get; init; }

    /// <summary>
    /// Gets the system architecture (e.g., x64, ARM64)
    /// </summary>
    [StringLength(20, ErrorMessage = "Architecture cannot exceed 20 characters")]
    [RegularExpression(@"^(x86|x64|ARM|ARM64|IA64)$", ErrorMessage = "Architecture must be one of: x86, x64, ARM, ARM64, IA64")]
    public string? Architecture { get; init; }

    /// <summary>
    /// Gets the version of the agent software
    /// </summary>
    [StringLength(50, ErrorMessage = "Version cannot exceed 50 characters")]
    [RegularExpression(@"^\d+\.\d+\.\d+(\.\d+)?(-[a-zA-Z0-9\-\.]+)?$", ErrorMessage = "Version must be in semantic version format (e.g., 1.0.0 or 1.0.0-beta)")]
    public string? Version { get; init; }

    /// <summary>
    /// Primary IP address
    /// </summary>
    [Required(ErrorMessage = "IpAddress is required")]
    [StringLength(45, MinimumLength = 7, ErrorMessage = "IpAddress must be between 7 and 45 characters")]
    [RegularExpression(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$|^(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}$",
        ErrorMessage = "IpAddress must be a valid IPv4 or IPv6 address")]
    public required string IpAddress { get; init; }

    /// <summary>
    /// Primary MAC address
    /// </summary>
    [StringLength(17, ErrorMessage = "MacAddress cannot exceed 17 characters")]
    [RegularExpression(@"^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$",
        ErrorMessage = "MacAddress must be in the format XX:XX:XX:XX:XX:XX or XX-XX-XX-XX-XX-XX")]
    public string? MacAddress { get; init; }

    /// <summary>
    /// Total system memory in MB
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "TotalMemoryMB must be a non-negative value")]
    public long TotalMemoryMB { get; init; }

    /// <summary>
    /// Number of processor cores
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ProcessorCores must be at least 1")]
    public int ProcessorCores { get; init; }

    /// <summary>
    /// Processor name/model
    /// </summary>
    [StringLength(500, ErrorMessage = "ProcessorName cannot exceed 500 characters")]
    public string? ProcessorName { get; init; }

    /// <summary>
    /// Domain the machine belongs to
    /// </summary>
    [StringLength(255, ErrorMessage = "Domain cannot exceed 255 characters")]
    public string? Domain { get; init; }

    /// <summary>
    /// Gets the timestamp when the agent was last seen
    /// </summary>
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets a value indicating whether the agent is currently online
    /// </summary>
    public bool IsOnline { get; init; } = true;

    /// <summary>
    /// Gets the collection of tags associated with the agent as a JSON string
    /// </summary>
    [StringLength(1000, ErrorMessage = "Tags cannot exceed 1000 characters")]
    public string? Tags { get; init; }

    /// <summary>
    /// Converts this registration request to an AgentDto
    /// </summary>
    public AgentDto ToAgentDto()
    {
        return new AgentDto
        {
            TenantId = TenantId,
            MachineName = MachineName,
            Domain = Domain,
            OperatingSystem = OperatingSystem,
            OSVersion = OSVersion,
            Architecture = Architecture,
            TotalMemoryMB = TotalMemoryMB,
            ProcessorCores = ProcessorCores,
            ProcessorName = ProcessorName,
            IpAddress = IpAddress,
            MacAddress = MacAddress,
            FirstSeen = Timestamp,
            LastSeen = LastSeen,
            Status = IsOnline ? AgentStatus.Online : AgentStatus.Offline,
            Version = Version,
            Tags = Tags
        };
    }
}

/// <summary>
/// Enhanced registration response with configuration details
/// </summary>
public record AgentRegistrationResponse : TenantScopedDto
{
    /// <summary>
    /// The registered agent response
    /// </summary>
    [Required]
    public required AgentResponse Agent { get; init; }

    /// <summary>
    /// Agent configuration details
    /// </summary>
    public Dictionary<string, object> Configuration { get; init; } = new();

    /// <summary>
    /// Registration status message
    /// </summary>
    [Required]
    public required string RegistrationStatus { get; init; }

    /// <summary>
    /// Additional message or instructions
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Agent heartbeat request DTO for MSP platform
/// Simple heartbeat to track agent status
/// </summary>
public record AgentHeartbeatRequest : TenantScopedDto
{
    /// <summary>
    /// Agent identifier sending the heartbeat
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    [StringLength(100, ErrorMessage = "AgentId cannot exceed 100 characters")]
    public required string AgentId { get; init; }
    
    /// <summary>
    /// Current status of the agent
    /// </summary>
    public AgentStatus Status { get; init; } = AgentStatus.Online;
    
    /// <summary>
    /// Optional message from the agent
    /// </summary>
    [StringLength(500, ErrorMessage = "Message cannot exceed 500 characters")]
    public string? Message { get; init; }
}

/// <summary>
/// Extension methods for AgentDto to support response creation
/// </summary>
public static class AgentDtoExtensions
{
    /// <summary>
    /// Create an agent response from this AgentDto
    /// </summary>
    public static AgentResponse CreateAgentResponse(this AgentDto agentDto, string? tenantName = null, string? groupName = null)
    {
        return AgentResponse.Create(agentDto, tenantName, groupName);
    }
}
