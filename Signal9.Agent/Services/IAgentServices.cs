using Signal9.Shared.DTOs;

namespace Signal9.Agent.Services;

/// <summary>
/// Interface for collecting telemetry data from the system
/// Clean, MSP-focused interface using our standardized DTOs
/// </summary>
public interface ITelemetryCollector
{
    Task<TelemetryDataDto> CollectTelemetryAsync();
    Task<TelemetryDataDto[]> CollectSpecificMetricsAsync(string[] metrics);
    Task<TelemetryDataDto> CollectTelemetryAsync(string[] metrics);
}

/// <summary>
/// Interface for getting system information
/// Consolidated into TelemetryDataDto for clean architecture
/// </summary>
public interface ISystemInfoProvider
{
    Task<TelemetryDataDto> GetSystemInfoAsync();
    Task<TelemetryDataDto> GetPerformanceMetricsAsync();
}
