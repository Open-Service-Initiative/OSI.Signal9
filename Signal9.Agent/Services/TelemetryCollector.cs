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
public class TelemetryCollector(
    ILogger<TelemetryCollector> logger,
    IOptions<AgentConfiguration> agentConfiguration,
    ISystemInfoProvider systemInfoProvider,
    Guid agentId) : ITelemetryCollector
{
    private readonly AgentConfiguration _agentConfiguration = agentConfiguration.Value;
    private readonly Guid _agentId = agentId;

    public async Task<TelemetryDataDto> CollectTelemetryAsync()
    {
        try
        {
            logger.LogDebug("Collecting telemetry data");

            var cpuUsage = await GetCpuUsageAsync();
            var (usedMb, availableMb) = GetMemoryInfo();
            var diskInfo = GetDiskInfo();

            return new TelemetryDataDto
            {
                AgentId = _agentId,
                TelemetryType = TelemetryType.SystemMetrics,
                CpuUsagePercent = cpuUsage,
                MemoryUsageMB = usedMb,
                AvailableMemoryMB = availableMb,
                DiskUsage = JsonSerializer.Serialize(diskInfo),
                ProcessCount = Process.GetProcesses().Length,
                UptimeSeconds = (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds,
                LoadAverage = Environment.ProcessorCount > 0 ? cpuUsage / 100.0 : 0
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error collecting telemetry data");
            return new TelemetryDataDto
            {
                AgentId = _agentId,
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
                logger.LogError(ex, "Error collecting metric: {Metric}", metric);
                results.Add(new TelemetryDataDto
                {
                    AgentId = _agentId,
                    TelemetryType = TelemetryType.SystemMetrics,
                    ErrorMessage = $"Error collecting {metric}: {ex.Message}"
                });
            }
        }

        return [..results];
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
                AgentId = _agentId,
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
            logger.LogError(ex, "Error collecting telemetry with specific metrics");
            return new TelemetryDataDto
            {
                AgentId = _agentId,
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
            "memory" => CollectMemoryMetric(),
            "disk" => await CollectDiskMetricAsync(),
            "process" => CollectProcessMetric(),
            "system" => await systemInfoProvider.GetSystemInfoAsync(),
            _ => await CollectTelemetryAsync()
        };
    }

    private async Task<TelemetryDataDto> CollectCpuMetricAsync()
    {
        var cpuUsage = await GetCpuUsageAsync();
        return new TelemetryDataDto
        {
            AgentId = _agentId,
            TelemetryType = TelemetryType.SystemMetrics,
            CpuUsagePercent = cpuUsage,
            LoadAverage = Environment.ProcessorCount > 0 ? cpuUsage / 100.0 : 0
        };
    }

    private TelemetryDataDto CollectMemoryMetric()
    {
        var (usedMb, availableMb) = GetMemoryInfo();
        return new TelemetryDataDto
        {
            AgentId = _agentId,
            TelemetryType = TelemetryType.SystemMetrics,
            MemoryUsageMB = usedMb,
            AvailableMemoryMB = availableMb
        };
    }

    private Task<TelemetryDataDto> CollectDiskMetricAsync()
    {
        var diskInfo = GetDiskInfo();
        return Task.FromResult(new TelemetryDataDto
        {
            AgentId = _agentId,
            TelemetryType = TelemetryType.SystemMetrics,
            DiskUsage = JsonSerializer.Serialize(diskInfo)
        });
    }

    private TelemetryDataDto CollectProcessMetric()
    {
        var processes = System.Diagnostics.Process.GetProcesses();
        return new TelemetryDataDto
        {
            AgentId = _agentId,
            TelemetryType = TelemetryType.SystemMetrics,
            ProcessCount = processes.Length,
            CustomMetrics = JsonSerializer.Serialize(processes.Take(10).Select(p => new
            {
                Name = p.ProcessName,
                p.Id,
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

    private (long UsedMB, long AvailableMB) GetMemoryInfo()
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

    private object GetDiskInfo()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new
                {
                    d.Name,
                    d.TotalSize,
                    AvailableSpace = d.AvailableFreeSpace,
                    UsagePercent = (double)(d.TotalSize - d.AvailableFreeSpace) / d.TotalSize * 100
                })
                .ToArray();

            return drives;
        }
        catch
        {
            return Array.Empty<object>();
        }
    }
}
