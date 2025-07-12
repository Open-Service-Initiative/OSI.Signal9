using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Data transfer object for agent registration
/// </summary>
public class AgentRegistrationDto
{
    public string AgentId { get; set; } = string.Empty;
    public string TenantCode { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool IsOnline { get; set; } = true;
    public Guid? TenantId { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Data transfer object for telemetry data
/// </summary>
public class TelemetryDto
{
    public string AgentId { get; set; } = string.Empty;
    public string TenantCode { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public DateTime Timestamp { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public double DiskUsage { get; set; }
    public double NetworkIn { get; set; }
    public double NetworkOut { get; set; }
    public SystemInfoDto SystemInfo { get; set; } = new();
    public Dictionary<string, object> CustomMetrics { get; set; } = new();
}

/// <summary>
/// Data transfer object for system information
/// </summary>
public class SystemInfoDto
{
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public int ProcessorCores { get; set; }
    public long TotalMemoryMB { get; set; }
    public long AvailableMemory { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string OSVersion { get; set; } = string.Empty;
    public string ProcessorName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public TimeSpan Uptime { get; set; }
    public List<DriveInfoDto> Drives { get; set; } = new();
}

/// <summary>
/// Data transfer object for performance metrics
/// </summary>
public class PerformanceMetricsDto
{
    public double CpuUsagePercent { get; set; }
    public long MemoryUsedMB { get; set; }
    public long MemoryAvailableMB { get; set; }
    public double DiskUsagePercent { get; set; }
    public long DiskFreeSpaceGB { get; set; }
    public double NetworkBytesInPerSec { get; set; }
    public double NetworkBytesOutPerSec { get; set; }
    public int ProcessCount { get; set; }
    public int ServiceCount { get; set; }
    public double SystemUptime { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Data transfer object for drive information
/// </summary>
public class DriveInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string DriveType { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }
    public double UsagePercent { get; set; }
    public bool IsReady { get; set; }
}

/// <summary>
/// Data transfer object for agent summary information
/// </summary>
public class AgentSummaryDto
{
    public string AgentId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; }
    public string Version { get; set; } = string.Empty;
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public bool IsOnline { get; set; }
}

/// <summary>
/// Data transfer object for agent heartbeat
/// </summary>
public class AgentHeartbeatDto
{
    public string AgentId { get; set; } = string.Empty;
    public string TenantCode { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public AgentStatus Status { get; set; }
    public Dictionary<string, object> StatusData { get; set; } = new();
}

/// <summary>
/// Data transfer object for agent commands
/// </summary>
public class CommandDto
{
    public string CommandId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public Dictionary<string, object>? Parameters { get; set; }
    public int Priority { get; set; } = 0;
    public DateTime? ExpiresAt { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Data transfer object for command results
/// </summary>
public class CommandResultDto
{
    public string CommandId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ExitCode { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Data transfer object for log entries
/// </summary>
public class LogEntryDto
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public Dictionary<string, object> Properties { get; set; } = new();
}
