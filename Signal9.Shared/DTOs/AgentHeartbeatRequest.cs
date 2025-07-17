using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Request DTO for agent heartbeat - sent periodically to indicate the agent is alive
/// </summary>
public record AgentHeartbeatRequest
{
    /// <summary>
    /// Unique identifier for this request
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    
    /// <summary>
    /// The tenant ID this agent belongs to (unified hierarchy)
    /// </summary>
    public Guid? ParentId { get; init; }
    /// <summary>
    /// Agent identifier sending the heartbeat
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    public required string AgentId { get; init; }
    
    /// <summary>
    /// Timestamp when the heartbeat was sent
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Current status of the agent
    /// </summary>
    public string? Status { get; init; }
    
    /// <summary>
    /// Current CPU usage percentage
    /// </summary>
    [Range(0, 100, ErrorMessage = "CpuUsage must be between 0 and 100")]
    public double? CpuUsage { get; init; }
    
    /// <summary>
    /// Current memory usage in MB
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "MemoryUsage must be non-negative")]
    public long? MemoryUsage { get; init; }
    
    /// <summary>
    /// Agent version information
    /// </summary>
    public string? Version { get; init; }
    
    // ParentId inherited from BaseDto<Guid> represents the tenant this agent belongs to
}
