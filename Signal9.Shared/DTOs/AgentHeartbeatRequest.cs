using System.ComponentModel.DataAnnotations;
using Signal9.Shared.Models;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Request DTO for agent heartbeat - simple status update
/// </summary>
public record AgentHeartbeatRequest : BaseDto<Guid>
{
    /// <summary>
    /// Agent identifier
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    public required string AgentId { get; init; }
    
    /// <summary>
    /// Heartbeat timestamp
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Current status
    /// </summary>
    public AgentStatus Status { get; init; } = AgentStatus.Online;
}