using System.ComponentModel.DataAnnotations;
using Signal9.Shared.Models;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Request DTO for agent registration - contains all information needed to register a new agent
/// Uses unified hierarchy where ParentId represents the tenant
/// </summary>
public record AgentRegistrationRequest : BaseDto<Guid>
{
    /// <summary>
    /// Agent identifier (machine-specific)
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    public required string AgentId { get; init; }
    
    /// <summary>
    /// Tenant code for organizational identification
    /// </summary>
    [Required(ErrorMessage = "TenantCode is required")]
    public required string TenantCode { get; init; }
    
    /// <summary>
    /// Machine name where the agent is running
    /// </summary>
    [Required(ErrorMessage = "MachineName is required")]
    public required string MachineName { get; init; }
    
    /// <summary>
    /// Operating system information
    /// </summary>
    [Required(ErrorMessage = "OperatingSystem is required")]
    public required string OperatingSystem { get; init; }
    
    /// <summary>
    /// OS version string
    /// </summary>
    [Required(ErrorMessage = "OSVersion is required")]
    public required string OSVersion { get; init; }
    
    /// <summary>
    /// Architecture (x64, x86, etc.)
    /// </summary>
    [Required(ErrorMessage = "Architecture is required")]
    public required string Architecture { get; init; }
    
    /// <summary>
    /// IP address of the agent machine
    /// </summary>
    public string? IpAddress { get; init; }
    
    /// <summary>
    /// MAC address of the primary network interface
    /// </summary>
    public string? MacAddress { get; init; }
    
    /// <summary>
    /// Total memory in MB
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "TotalMemoryMB must be non-negative")]
    public long TotalMemoryMB { get; init; }
    
    /// <summary>
    /// Number of processor cores
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ProcessorCores must be positive")]
    public int ProcessorCores { get; init; }
    
    /// <summary>
    /// Processor name/model
    /// </summary>
    public string? ProcessorName { get; init; }
    
    /// <summary>
    /// Domain the machine belongs to
    /// </summary>
    public string? Domain { get; init; }
    
    /// <summary>
    /// Last seen timestamp
    /// </summary>
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Agent version
    /// </summary>
    [Required(ErrorMessage = "Version is required")]
    public required string Version { get; init; }
    
    /// <summary>
    /// Whether the agent is currently online
    /// </summary>
    public bool IsOnline { get; init; } = true;
}