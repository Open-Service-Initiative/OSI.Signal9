using Microsoft.Extensions.Logging;
using Signal9.Shared.DTOs;
using System.Diagnostics;

namespace Signal9.Agent.Services;

/// <summary>
/// Service for collecting system telemetry data
/// </summary>
public class TelemetryCollector : ITelemetryCollector
{
    private readonly ILogger<TelemetryCollector> _logger;
    private readonly ISystemInfoProvider _systemInfoProvider;

    public TelemetryCollector(ILogger<TelemetryCollector> logger, ISystemInfoProvider systemInfoProvider)
    {
        _logger = logger;
        _systemInfoProvider = systemInfoProvider;
    }

    public async Task<TelemetryDto> CollectTelemetryAsync()
    {
        try
        {
            var performanceMetrics = await _systemInfoProvider.GetPerformanceMetricsAsync().ConfigureAwait(false);
            var systemInfo = await _systemInfoProvider.GetSystemInfoAsync().ConfigureAwait(false);
            
            var telemetry = new TelemetryDto
            {
                AgentId = Environment.MachineName,
                TenantCode = "default",
                Timestamp = DateTime.UtcNow,
                CpuUsage = performanceMetrics.CpuUsagePercent,
                MemoryUsage = performanceMetrics.MemoryUsedMB,
                DiskUsage = performanceMetrics.DiskUsagePercent,
                NetworkIn = performanceMetrics.NetworkBytesInPerSec,
                NetworkOut = performanceMetrics.NetworkBytesOutPerSec,
                SystemInfo = systemInfo,
                CustomMetrics = new Dictionary<string, object>
                {
                    { "CollectionTime", DateTime.UtcNow },
                    { "AgentVersion", "1.0.0" },
                    { "ProcessCount", performanceMetrics.ProcessCount },
                    { "ServiceCount", performanceMetrics.ServiceCount },
                    { "SystemUptime", performanceMetrics.SystemUptime }
                }
            };

            return telemetry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting telemetry");
            return new TelemetryDto 
            {
                AgentId = Environment.MachineName,
                TenantCode = "default",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    public async Task<TelemetryDto[]> CollectSpecificMetricsAsync(string[] metrics)
    {
        try
        {
            var results = new List<TelemetryDto>();
            
            foreach (var metric in metrics)
            {
                var telemetry = await CollectTelemetryAsync().ConfigureAwait(false);
                
                // Filter telemetry based on requested metric
                switch (metric.ToLower())
                {
                    case "cpu":
                        telemetry.CustomMetrics = new Dictionary<string, object> { { "CpuUsage", telemetry.CpuUsage } };
                        break;
                    case "memory":
                        telemetry.CustomMetrics = new Dictionary<string, object> { { "MemoryUsage", telemetry.MemoryUsage } };
                        break;
                    case "disk":
                        telemetry.CustomMetrics = new Dictionary<string, object> { { "DiskUsage", telemetry.DiskUsage } };
                        break;
                    case "network":
                        telemetry.CustomMetrics = new Dictionary<string, object> 
                        { 
                            { "NetworkIn", telemetry.NetworkIn },
                            { "NetworkOut", telemetry.NetworkOut }
                        };
                        break;
                }
                
                results.Add(telemetry);
            }
            
            return results.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting specific metrics: {Metrics}", string.Join(", ", metrics));
            return Array.Empty<TelemetryDto>();
        }
    }

    public async Task<TelemetryDto> CollectTelemetryAsync(string[] metrics)
    {
        // This method is for collecting telemetry with specific focus on certain metrics
        var telemetry = await CollectTelemetryAsync().ConfigureAwait(false);
        
        if (metrics?.Length > 0)
        {
            // Filter the telemetry to only include requested metrics
            var filteredMetrics = new Dictionary<string, object>();
            
            foreach (var metric in metrics)
            {
                switch (metric.ToLower())
                {
                    case "cpu":
                        filteredMetrics["CpuUsage"] = telemetry.CpuUsage;
                        break;
                    case "memory":
                        filteredMetrics["MemoryUsage"] = telemetry.MemoryUsage;
                        break;
                    case "disk":
                        filteredMetrics["DiskUsage"] = telemetry.DiskUsage;
                        break;
                    case "network":
                        filteredMetrics["NetworkIn"] = telemetry.NetworkIn;
                        filteredMetrics["NetworkOut"] = telemetry.NetworkOut;
                        break;
                }
            }
            
            telemetry.CustomMetrics = filteredMetrics;
        }
        
        return telemetry;
    }
}
