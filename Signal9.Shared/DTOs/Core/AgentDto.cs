using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.Contracts;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Agent DTO - represents a managed endpoint in the Signal9 RMM system.
/// Uses unified hierarchy where ParentId represents the tenant this agent belongs to.
/// </summary>
public record AgentDto : BaseDto<Guid>, IAgentData
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
    
    // ParentId inherited from BaseDto<Guid> represents the tenant this agent belongs to
}


