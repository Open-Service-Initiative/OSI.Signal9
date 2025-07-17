using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.Contracts;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Agent command DTO - represents commands sent to agents for execution.
/// Uses unified hierarchy where ParentId represents the agent that should execute this command.
/// </summary>
public record AgentCommandDto : BaseDto<Guid>, IAgentCommandData
{
    /// <summary>
    /// Target agent ID (same as ParentId for unified hierarchy)
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
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
    
    // ParentId inherited from BaseDto<Guid> represents the agent that should execute this command
}


