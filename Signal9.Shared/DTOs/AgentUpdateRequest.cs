using System.ComponentModel.DataAnnotations;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Request DTO for updating agent information
/// </summary>
public record AgentUpdateRequest
{
    /// <summary>
    /// Unique identifier for this request
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    
    /// <summary>
    /// The tenant ID this agent belongs to (for reassignment) - Using ParentId for unified hierarchy
    /// </summary>
    public Guid? ParentId { get; init; }
    
    /// <summary>
    /// Updated machine name
    /// </summary>
    public string? MachineName { get; init; }
    
    /// <summary>
    /// Updated IP address
    /// </summary>
    public string? IpAddress { get; init; }
    
    /// <summary>
    /// Updated MAC address
    /// </summary>
    public string? MacAddress { get; init; }
    
    /// <summary>
    /// Updated operating system information
    /// </summary>
    public string? OperatingSystem { get; init; }
    
    /// <summary>
    /// Updated OS version
    /// </summary>
    public string? OSVersion { get; init; }
    
    /// <summary>
    /// Updated architecture
    /// </summary>
    public string? Architecture { get; init; }
    
    /// <summary>
    /// Updated total memory in MB
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "TotalMemoryMB must be non-negative")]
    public long? TotalMemoryMB { get; init; }
    
    /// <summary>
    /// Updated processor cores count
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ProcessorCores must be positive")]
    public int? ProcessorCores { get; init; }
    
    /// <summary>
    /// Updated processor name
    /// </summary>
    public string? ProcessorName { get; init; }
    
    /// <summary>
    /// Updated domain
    /// </summary>
    public string? Domain { get; init; }
    
    /// <summary>
    /// Updated version
    /// </summary>
    public string? Version { get; init; }
    
    /// <summary>
    /// Updated description
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Updated tags/group information
    /// </summary>
    public string? Tags { get; init; }
    
    /// <summary>
    /// Updated group name for UI display
    /// </summary>
    public string? GroupName { get; init; }
    
    /// <summary>
    /// Updated agent status
    /// </summary>
    public AgentStatus? Status { get; init; }
    
    /// <summary>
    /// Updated online status
    /// </summary>
    public bool? IsOnline { get; init; }
    
    /// <summary>
    /// Last seen timestamp
    /// </summary>
    public DateTime? LastSeen { get; init; }
    
    // ParentId inherited from BaseDto<Guid> represents the tenant this agent belongs to (for reassignment)
}
