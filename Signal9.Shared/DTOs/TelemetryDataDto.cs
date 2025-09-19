using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.Contracts;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Telemetry data DTO - represents performance and system metrics from agents.
/// Uses unified hierarchy where ParentId represents the agent that generated this telemetry.
/// </summary>
public record TelemetryDataDto : BaseDto<Guid>, ITelemetryData
{
    /// <summary>
    /// Agent ID that generated this telemetry (same as ParentId for unified hierarchy)
    /// </summary>
    [Required(ErrorMessage = "AgentId is required")]
    public required Guid AgentId { get; init; }
    
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
    
    // ParentId inherited from BaseDto<Guid> represents the agent that generated this telemetry
}


