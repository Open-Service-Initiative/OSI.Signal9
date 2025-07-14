using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Signal9.Shared.Configuration;
using Signal9.Shared.DTOs;
using Signal9.Shared.Models;
using System.Text.Json;

namespace Signal9.Agent.Services;

/// <summary>
/// Service for collecting telemetry data from the system
/// Clean MSP-focused implementation using TelemetryDataDto
/// </summary>
public class TelemetryCollector : ITelemetryCollector
{
    private readonly ILogger<TelemetryCollector> _logger;
    private readonly AgentConfiguration _agentConfiguration;
    private readonly ISystemInfoProvider _systemInfoProvider;

    public TelemetryCollector(
        ILogger<TelemetryCollector> logger,
        IOptions<AgentConfiguration> agentConfiguration,
        ISystemInfoProvider systemInfoProvider)
    {
        _logger = logger;
        _agentConfiguration = agentConfiguration.Value;
        _systemInfoProvider = systemInfoProvider;
    }

    public async Task<TelemetryDataDto> CollectTelemetryAsync()
    {
        try
        {
            _logger.LogDebug("Collecting telemetry data");

            var cpuUsage = await GetCpuUsageAsync();
            var memoryInfo = await GetMemoryInfoAsync();
            var diskInfo = await GetDiskInfoAsync();

            return new TelemetryDataDto
            {
                TenantId = Guid.Empty, // Will be set by calling service
                AgentId = Environment.MachineName, // Will be set by calling service
                TelemetryType = TelemetryType.SystemMetrics,
                CpuUsagePercent = cpuUsage,
                MemoryUsageMB = memoryInfo.UsedMB,
                AvailableMemoryMB = memoryInfo.AvailableMB,
                DiskUsage = JsonSerializer.Serialize(diskInfo),
                ProcessCount = System.Diagnostics.Process.GetProcesses().Length,
                UptimeSeconds = (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds,
                LoadAverage = Environment.ProcessorCount > 0 ? cpuUsage / 100.0 : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting telemetry data");
            return new TelemetryDataDto
            {
                TenantId = Guid.Empty,
                AgentId = Environment.MachineName,
                TelemetryType = TelemetryType.SystemMetrics,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<TelemetryDataDto[]> CollectSpecificMetricsAsync(string[] metrics)
    {
        var results = new List<TelemetryDataDto>();

        foreach (var metric in metrics)
        {
            try
            {
                var telemetryData = await CollectSpecificMetricAsync(metric);
                results.Add(telemetryData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting metric: {Metric}", metric);
                results.Add(new TelemetryDataDto
                {
                    TenantId = Guid.Empty,
                    AgentId = Environment.MachineName,
                    TelemetryType = TelemetryType.SystemMetrics,
                    ErrorMessage = $"Error collecting {metric}: {ex.Message}"
                });
            }
        }

        return results.ToArray();
    }

    public async Task<TelemetryDataDto> CollectTelemetryAsync(string[] metrics)
    {
        try
        {
            if (metrics == null || metrics.Length == 0)
            {
                return await CollectTelemetryAsync();
            }

            var allMetrics = await CollectSpecificMetricsAsync(metrics);
            
            // Aggregate the metrics into a single TelemetryDataDto
            var aggregated = new TelemetryDataDto
            {
                TenantId = Guid.Empty,
                AgentId = Environment.MachineName,
                TelemetryType = TelemetryType.SystemMetrics,
                CpuUsagePercent = allMetrics.FirstOrDefault()?.CpuUsagePercent,
                MemoryUsageMB = allMetrics.FirstOrDefault()?.MemoryUsageMB,
                AvailableMemoryMB = allMetrics.FirstOrDefault()?.AvailableMemoryMB,
                ProcessCount = allMetrics.FirstOrDefault()?.ProcessCount,
                UptimeSeconds = allMetrics.FirstOrDefault()?.UptimeSeconds,
                LoadAverage = allMetrics.FirstOrDefault()?.LoadAverage,
                CustomMetrics = JsonSerializer.Serialize(allMetrics.Where(m => !string.IsNullOrEmpty(m.CustomMetrics)))
            };

            return aggregated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting telemetry with specific metrics");
            return new TelemetryDataDto
            {
                TenantId = Guid.Empty,
                AgentId = Environment.MachineName,
                TelemetryType = TelemetryType.SystemMetrics,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<TelemetryDataDto> CollectSpecificMetricAsync(string metric)
    {
        return metric.ToLowerInvariant() switch
        {
            "cpu" => await CollectCpuMetricAsync(),
            "memory" => await CollectMemoryMetricAsync(),
            "disk" => await CollectDiskMetricAsync(),
            "process" => await CollectProcessMetricAsync(),
            "system" => await _systemInfoProvider.GetSystemInfoAsync(),
            _ => await CollectTelemetryAsync()
        };
    }

    private async Task<TelemetryDataDto> CollectCpuMetricAsync()
    {
        var cpuUsage = await GetCpuUsageAsync();
        return new TelemetryDataDto
        {
            TenantId = Guid.Empty,
            AgentId = Environment.MachineName,
            TelemetryType = TelemetryType.SystemMetrics,
            CpuUsagePercent = cpuUsage,
            LoadAverage = Environment.ProcessorCount > 0 ? cpuUsage / 100.0 : 0
        };
    }

    private async Task<TelemetryDataDto> CollectMemoryMetricAsync()
    {
        var memoryInfo = await GetMemoryInfoAsync();
        return new TelemetryDataDto
        {
            TenantId = Guid.Empty,
            AgentId = Environment.MachineName,
            TelemetryType = TelemetryType.SystemMetrics,
            MemoryUsageMB = memoryInfo.UsedMB,
            AvailableMemoryMB = memoryInfo.AvailableMB
        };
    }

    private async Task<TelemetryDataDto> CollectDiskMetricAsync()
    {
        var diskInfo = await GetDiskInfoAsync();
        return new TelemetryDataDto
        {
            TenantId = Guid.Empty,
            AgentId = Environment.MachineName,
            TelemetryType = TelemetryType.SystemMetrics,
            DiskUsage = JsonSerializer.Serialize(diskInfo)
        };
    }

    private async Task<TelemetryDataDto> CollectProcessMetricAsync()
    {
        var processes = System.Diagnostics.Process.GetProcesses();
        return new TelemetryDataDto
        {
            TenantId = Guid.Empty,
            AgentId = Environment.MachineName,
            TelemetryType = TelemetryType.SystemMetrics,
            ProcessCount = processes.Length,
            CustomMetrics = JsonSerializer.Serialize(processes.Take(10).Select(p => new
            {
                Name = p.ProcessName,
                Id = p.Id,
                WorkingSet = p.WorkingSet64 / 1024 / 1024, // MB
                CpuTime = p.TotalProcessorTime.TotalMilliseconds
            }))
        };
    }

    private async Task<double> GetCpuUsageAsync()
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var startCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;
            
            await Task.Delay(500); // Sample for 500ms
            
            var endTime = DateTime.UtcNow;
            var endCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;
            
            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
            
            return Math.Min(cpuUsageTotal * 100, 100);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<(long UsedMB, long AvailableMB)> GetMemoryInfoAsync()
    {
        try
        {
            var gcMemoryInfo = GC.GetGCMemoryInfo();
            var workingSet = Environment.WorkingSet;
            var availableMemory = gcMemoryInfo.TotalAvailableMemoryBytes;
            
            return (workingSet / 1024 / 1024, availableMemory / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    private async Task<object> GetDiskInfoAsync()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new
                {
                    Name = d.Name,
                    TotalSize = d.TotalSize,
                    AvailableSpace = d.AvailableFreeSpace,
                    UsagePercent = (double)(d.TotalSize - d.AvailableFreeSpace) / d.TotalSize * 100
                })
                .ToArray();
            
            return drives;
        }
        catch
        {
            return new object[0];
        }
    }
}
