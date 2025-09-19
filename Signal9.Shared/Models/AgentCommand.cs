using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Signal9.Shared.Models.Base;
using Signal9.Shared.Contracts;

namespace Signal9.Shared.Models;

/// <summary>
/// Agent command entity optimized for Azure SQL Database
/// Stores commands to be executed by agents with proper tracking
/// </summary>
[Table("agent_commands")]
[Index(nameof(ParentId), nameof(AgentId), nameof(Status))]
[Index(nameof(ParentId), nameof(CommandType))]
public class AgentCommand : BaseSqlEntity, IAgentCommandData
{
    /// <summary>
    /// Target agent ID
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Column("agent_id")]
    public Guid AgentId { get; set; }
    
    /// <summary>
    /// Type of command to execute
    /// </summary>
    [Required]
    [Column("command_type")]
    public CommandType CommandType { get; set; }
    
    /// <summary>
    /// Command parameters (JSON serialized)
    /// </summary>
    [Column("parameters")]
    public string? Parameters { get; set; }
    
    /// <summary>
    /// Current execution status
    /// </summary>
    [Column("status")]
    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    
    /// <summary>
    /// When the command was scheduled
    /// </summary>
    [Column("scheduled_at")]
    public DateTime ScheduledAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the command execution started
    /// </summary>
    [Column("started_at")]
    public DateTime? StartedAt { get; set; }
    
    /// <summary>
    /// When the command execution completed
    /// </summary>
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }
    
    /// <summary>
    /// Command execution result
    /// </summary>
    [Column("result")]
    public string? Result { get; set; }
    
    /// <summary>
    /// Error message if command failed
    /// </summary>
    [Column("error_message")]
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// User who initiated the command
    /// </summary>
    [MaxLength(100)]
    [Column("initiated_by")]
    public string? InitiatedBy { get; set; }
    
    /// <summary>
    /// Command timeout in seconds
    /// </summary>
    [Column("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 300; // 5 minutes default
    
    /// <summary>
    /// Command priority
    /// </summary>
    [Column("priority")]
    public CommandPriority Priority { get; set; } = CommandPriority.Normal;

    public AgentCommand() : base() { }
    
    public AgentCommand(Guid parentId, Guid agentId, CommandType commandType) 
        : base(parentId)
    {
        AgentId = agentId;
        CommandType = commandType;
        ScheduledAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Types of commands that can be executed
/// </summary>
public enum CommandType
{
    RestartService = 0,
    UpdateAgent = 1,
    CollectLogs = 2,
    RunScript = 3,
    InstallSoftware = 4,
    UninstallSoftware = 5,
    SystemRestart = 6,
    SystemShutdown = 7,
    FileTransfer = 8,
    RegistryModification = 9,
    NetworkDiagnostics = 10,
    SecurityScan = 11,
    CustomCommand = 99
}

/// <summary>
/// Command execution status
/// </summary>
public enum CommandStatus
{
    Pending = 0,
    Sent = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    Timeout = 6
}

/// <summary>
/// Command execution priority
/// </summary>
public enum CommandPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}
