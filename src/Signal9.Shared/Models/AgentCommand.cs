using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.Models;

/// <summary>
/// Represents a command that can be executed by an agent
/// </summary>
public class AgentCommand
{
    /// <summary>
    /// Unique identifier for the command
    /// </summary>
    [Required]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Type of command to execute
    /// </summary>
    [Required]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Command type for backwards compatibility
    /// </summary>
    public string CommandType 
    { 
        get => Type; 
        set => Type = value; 
    }

    /// <summary>
    /// Parameters for the command
    /// </summary>
    public Dictionary<string, object>? Parameters { get; set; }

    /// <summary>
    /// Priority of the command (0 = highest, higher numbers = lower priority)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// When the command expires and should no longer be executed
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// When the command was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Agent ID that should execute this command
    /// </summary>
    public string? TargetAgentId { get; set; }

    /// <summary>
    /// Current status of the command
    /// </summary>
    public AgentCommandStatus Status { get; set; } = AgentCommandStatus.Pending;

    /// <summary>
    /// Additional metadata for the command
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Status of an agent command
/// </summary>
public enum AgentCommandStatus
{
    /// <summary>
    /// Command is waiting to be executed
    /// </summary>
    Pending,

    /// <summary>
    /// Command is currently being executed
    /// </summary>
    Running,

    /// <summary>
    /// Command completed successfully
    /// </summary>
    Completed,

    /// <summary>
    /// Command failed during execution
    /// </summary>
    Failed,

    /// <summary>
    /// Command was cancelled
    /// </summary>
    Cancelled,

    /// <summary>
    /// Command expired before execution
    /// </summary>
    Expired
}
