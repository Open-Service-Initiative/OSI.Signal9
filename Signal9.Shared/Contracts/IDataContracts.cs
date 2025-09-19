using Signal9.Shared.Models;

namespace Signal9.Shared.Contracts;

/// <summary>
/// Shared contract for agent data across entities and DTOs
/// Defines the core properties that all agent representations must have
/// </summary>
public interface IAgentData
{
    string MachineName { get; }
    string? Domain { get; }
    string OperatingSystem { get; }
    string? OSVersion { get; }
    string? Architecture { get; }
    long TotalMemoryMB { get; }
    int ProcessorCores { get; }
    string? ProcessorName { get; }
    string IpAddress { get; }
    string? MacAddress { get; }
    DateTime FirstSeen { get; }
    DateTime LastSeen { get; }
    AgentStatus Status { get; }
    string? Version { get; }
    string? Tags { get; }
}

/// <summary>
/// Shared contract for telemetry data across entities and DTOs
/// </summary>
public interface ITelemetryData
{
    Guid AgentId { get; }
    TelemetryType TelemetryType { get; }
    double? CpuUsagePercent { get; }
    long? MemoryUsageMB { get; }
    long? AvailableMemoryMB { get; }
    string? DiskUsage { get; }
    string? NetworkInterfaces { get; }
    int? ProcessCount { get; }
    long? UptimeSeconds { get; }
    double? LoadAverage { get; }
    string? TemperatureReadings { get; }
    string? CustomMetrics { get; }
    string? ErrorMessage { get; }
}

/// <summary>
/// Shared contract for agent command data across entities and DTOs
/// </summary>
public interface IAgentCommandData
{
    Guid AgentId { get; }
    CommandType CommandType { get; }
    string? Parameters { get; }
    CommandStatus Status { get; }
    DateTime ScheduledAt { get; }
    DateTime? StartedAt { get; }
    DateTime? CompletedAt { get; }
    string? Result { get; }
    string? ErrorMessage { get; }
    string? InitiatedBy { get; }
    int TimeoutSeconds { get; }
    CommandPriority Priority { get; }
}

/// <summary>
/// Shared contract for tenant data across entities and DTOs
/// </summary>
public interface ITenantData
{
    string TenantCode { get; }
    string Name { get; }
    string? Description { get; }
    string ContactEmail { get; }
    SubscriptionTier SubscriptionTier { get; }
    int MaxAgents { get; }
    int DataRetentionDays { get; }
    string? Settings { get; }
    TenantStatus Status { get; }
    DateTime? SubscriptionExpiresAt { get; }
    string? ApiKey { get; }
}
