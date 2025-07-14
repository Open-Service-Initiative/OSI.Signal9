using Signal9.Shared.Models.Base;
using Signal9.Shared.Contracts;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.Models;

/// <summary>
/// Telemetry data entity optimized for Azure Tables
/// Uses TenantId as PartitionKey for optimal scaling and tenant isolation
/// RowKey combines AgentId and timestamp for time-series queries
/// </summary>
public class TelemetryData : BaseTableEntity, ITelemetryData
{
    /// <summary>
    /// Agent ID that generated this telemetry
    /// </summary>
    [Required]
    public string AgentId { get; set; } = string.Empty;
    
    /// <summary>
    /// Type of telemetry data
    /// </summary>
    [Required]
    public TelemetryType TelemetryType { get; set; }
    
    /// <summary>
    /// CPU usage percentage (0-100)
    /// </summary>
    public double? CpuUsagePercent { get; set; }
    
    /// <summary>
    /// Memory usage in MB
    /// </summary>
    public long? MemoryUsageMB { get; set; }
    
    /// <summary>
    /// Available memory in MB
    /// </summary>
    public long? AvailableMemoryMB { get; set; }
    
    /// <summary>
    /// Disk usage information (JSON serialized)
    /// </summary>
    public string? DiskUsage { get; set; }
    
    /// <summary>
    /// Network interfaces information (JSON serialized)
    /// </summary>
    public string? NetworkInterfaces { get; set; }
    
    /// <summary>
    /// Running processes count
    /// </summary>
    public int? ProcessCount { get; set; }
    
    /// <summary>
    /// System uptime in seconds
    /// </summary>
    public long? UptimeSeconds { get; set; }
    
    /// <summary>
    /// System load average (Linux/Mac)
    /// </summary>
    public double? LoadAverage { get; set; }
    
    /// <summary>
    /// Temperature readings (JSON serialized)
    /// </summary>
    public string? TemperatureReadings { get; set; }
    
    /// <summary>
    /// Custom metrics (JSON serialized)
    /// </summary>
    public string? CustomMetrics { get; set; }
    
    /// <summary>
    /// Error message if telemetry collection failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    public TelemetryData() : base() 
    {
        // Create time-based RowKey for efficient time-series queries
        // Format: {AgentId}_{ReverseTicks} for reverse chronological order
        Id = $"{AgentId}_{DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks:D19}";
    }
    
    public TelemetryData(string tenantId, string agentId, TelemetryType telemetryType) 
        : base(tenantId)
    {
        AgentId = agentId;
        TelemetryType = telemetryType;
        // Create time-based RowKey for efficient time-series queries
        Id = $"{agentId}_{DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks:D19}";
    }
}

/// <summary>
/// Types of telemetry data
/// </summary>
public enum TelemetryType
{
    SystemMetrics = 0,
    ProcessMetrics = 1,
    NetworkMetrics = 2,
    DiskMetrics = 3,
    SecurityEvents = 4,
    ApplicationLogs = 5,
    CustomMetrics = 6
}
