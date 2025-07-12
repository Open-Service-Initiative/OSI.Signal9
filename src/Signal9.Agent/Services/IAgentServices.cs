using Signal9.Shared.DTOs;

namespace Signal9.Agent.Services;

/// <summary>
/// Interface for collecting telemetry data from the system
/// </summary>
public interface ITelemetryCollector
{
    Task<TelemetryDto> CollectTelemetryAsync();
    Task<TelemetryDto[]> CollectSpecificMetricsAsync(string[] metrics);
    Task<TelemetryDto> CollectTelemetryAsync(string[] metrics);
}

/// <summary>
/// Interface for getting system information
/// </summary>
public interface ISystemInfoProvider
{
    Task<SystemInfoDto> GetSystemInfoAsync();
    Task<PerformanceMetricsDto> GetPerformanceMetricsAsync();
}
