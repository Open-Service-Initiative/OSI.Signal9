using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Signal9.Shared.Models.Base;
using Signal9.Shared.Contracts;

namespace Signal9.Shared.Models;

/// <summary>
/// Agent entity optimized for Azure SQL Database
/// Stores core agent information with relational integrity
/// </summary>
[Table("agents")]
[Index(nameof(ParentId), nameof(MachineName), IsUnique = true)]
[Index(nameof(ParentId), nameof(Status))]
public class Agent : BaseSqlEntity, IAgentData
{
    /// <summary>
    /// Machine name of the agent
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Column("machine_name")]
    public string MachineName { get; set; } = string.Empty;
    
    /// <summary>
    /// Domain the machine belongs to
    /// </summary>
    [MaxLength(255)]
    [Column("domain")]
    public string? Domain { get; set; }
    
    /// <summary>
    /// Operating system type
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Column("operating_system")]
    public string OperatingSystem { get; set; } = string.Empty;
    
    /// <summary>
    /// Operating system version
    /// </summary>
    [MaxLength(50)]
    [Column("os_version")]
    public string? OSVersion { get; set; }
    
    /// <summary>
    /// System architecture (x64, x86, ARM64)
    /// </summary>
    [MaxLength(20)]
    [Column("architecture")]
    public string? Architecture { get; set; }
    
    /// <summary>
    /// Total system memory in MB
    /// </summary>
    [Column("total_memory_mb")]
    public long TotalMemoryMB { get; set; }
    
    /// <summary>
    /// Number of processor cores
    /// </summary>
    [Column("processor_cores")]
    public int ProcessorCores { get; set; }
    
    /// <summary>
    /// Processor name/model
    /// </summary>
    [MaxLength(500)]
    [Column("processor_name")]
    public string? ProcessorName { get; set; }
    
    /// <summary>
    /// Primary IP address
    /// </summary>
    [Required]
    [MaxLength(45)]
    [Column("ip_address")]
    public string IpAddress { get; set; } = string.Empty;
    
    /// <summary>
    /// Primary MAC address
    /// </summary>
    [MaxLength(17)]
    [Column("mac_address")]
    public string? MacAddress { get; set; }
    
    /// <summary>
    /// When the agent was first seen
    /// </summary>
    [Column("first_seen")]
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Last time the agent checked in
    /// </summary>
    [Column("last_seen")]
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Current agent status
    /// </summary>
    [Column("status")]
    public AgentStatus Status { get; set; } = AgentStatus.Online;
    
    /// <summary>
    /// Agent version
    /// </summary>
    [MaxLength(50)]
    [Column("version")]
    public string? Version { get; set; }
    
    /// <summary>
    /// Tags for organization and filtering
    /// </summary>
    [MaxLength(1000)]
    [Column("tags")]
    public string? Tags { get; set; }

    public Agent() { }
    
    public Agent(Guid ParentId) { }
}

/// <summary>
/// Agent status enumeration
/// </summary>
public enum AgentStatus
{
    Online = 0,
    Offline = 1,
    Maintenance = 2,
    Error = 3,
    Unregistered = 4
}
