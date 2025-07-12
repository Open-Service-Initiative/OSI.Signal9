using Microsoft.Extensions.Logging;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Buffers.Text;
using System.Text;
using Signal9.Shared.DTOs;

namespace Signal9.Agent.Services;

/// <summary>
/// Service for collecting system information with .NET 9 performance optimizations
/// </summary>
public class SystemInfoProvider : ISystemInfoProvider
{
    private readonly ILogger<SystemInfoProvider> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SystemInfoProvider(ILogger<SystemInfoProvider> logger)
    {
        _logger = logger;
    }

    public async Task<SystemInfoDto> GetSystemInfoAsync()
    {
        // Use semaphore to prevent concurrent system info collection
        await _semaphore.WaitAsync();
        try
        {
            var systemInfo = new SystemInfoDto
            {
                MachineName = Environment.MachineName,
                Domain = Environment.UserDomainName,
                OperatingSystem = Environment.OSVersion.Platform.ToString(),
                OSVersion = Environment.OSVersion.VersionString,
                Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                ProcessorCores = Environment.ProcessorCount,
                Version = "1.0.0"
            };

            // Use ConfigureAwait(false) for better performance in library code
            systemInfo.TotalMemoryMB = await GetTotalMemoryAsync().ConfigureAwait(false);
            systemInfo.ProcessorName = await GetProcessorNameAsync().ConfigureAwait(false);
            systemInfo.IpAddress = await GetIpAddressAsync().ConfigureAwait(false);
            systemInfo.MacAddress = await GetMacAddressAsync().ConfigureAwait(false);

            return systemInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system information");
            return new SystemInfoDto();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<long> GetTotalMemoryAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            return await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var totalMemory = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                    return totalMemory / 1024 / 1024; // Convert to MB
                }
                return 0L;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting total memory");
            return 0;
        }
    }

    private async Task<string> GetProcessorNameAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Unknown";
        }

        try
        {
            return await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    return obj["Name"]?.ToString() ?? "Unknown";
                }
                return "Unknown";
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processor name");
            return "Unknown";
        }
    }

    private async Task<string> GetIpAddressAsync()
    {
        try
        {
            var host = await Dns.GetHostEntryAsync(Dns.GetHostName()).ConfigureAwait(false);
            var ipAddress = host.AddressList
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return ipAddress?.ToString() ?? "Unknown";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting IP address");
            return "Unknown";
        }
    }

    private async Task<string> GetMacAddressAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && 
                                       n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
                
                return networkInterface?.GetPhysicalAddress().ToString() ?? "Unknown";
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting MAC address");
            return "Unknown";
        }
    }

    public async Task<PerformanceMetricsDto> GetPerformanceMetricsAsync()
    {
        try
        {
            var metrics = new PerformanceMetricsDto
            {
                CpuUsagePercent = await GetCpuUsageAsync().ConfigureAwait(false),
                MemoryUsedMB = GetMemoryUsed(),
                MemoryAvailableMB = GetMemoryAvailable(),
                DiskUsagePercent = GetDiskUsage(),
                DiskFreeSpaceGB = GetDiskFreeSpace(),
                NetworkBytesInPerSec = GetNetworkBytesIn(),
                NetworkBytesOutPerSec = GetNetworkBytesOut(),
                ProcessCount = System.Diagnostics.Process.GetProcesses().Length,
                ServiceCount = GetServiceCount(),
                SystemUptime = GetSystemUptime()
            };

            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting performance metrics");
            throw;
        }
    }

    private async Task<double> GetCpuUsageAsync()
    {
        try
        {
            // Simple CPU usage estimation - not perfectly accurate but functional
            var startTime = DateTime.UtcNow;
            var startCpuUsage = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            await Task.Delay(1000);
            var endTime = DateTime.UtcNow;
            var endCpuUsage = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
            return Math.Min(cpuUsageTotal * 100, 100);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get CPU usage");
            return 0;
        }
    }

    private long GetMemoryUsed()
    {
        try
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.WorkingSet64 / (1024 * 1024);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get memory used");
            return 0;
        }
    }

    private long GetMemoryAvailable()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher("SELECT AvailableBytes FROM Win32_PerfRawData_PerfOS_Memory");
                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    return Convert.ToInt64(obj["AvailableBytes"]) / (1024 * 1024);
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get available memory");
            return 0;
        }
    }

    private double GetDiskUsage()
    {
        try
        {
            var drives = DriveInfo.GetDrives();
            var totalSize = drives.Sum(d => d.IsReady ? d.TotalSize : 0);
            var freeSpace = drives.Sum(d => d.IsReady ? d.TotalFreeSpace : 0);
            var usedSpace = totalSize - freeSpace;
            return totalSize > 0 ? (double)usedSpace / totalSize * 100 : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get disk usage");
            return 0;
        }
    }

    private long GetDiskFreeSpace()
    {
        try
        {
            var drives = DriveInfo.GetDrives();
            var freeSpace = drives.Sum(d => d.IsReady ? d.TotalFreeSpace : 0);
            return freeSpace / (1024 * 1024 * 1024);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get disk free space");
            return 0;
        }
    }

    private double GetNetworkBytesIn()
    {
        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            var totalBytesReceived = networkInterfaces
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Sum(ni => ni.GetIPStatistics().BytesReceived);
            return totalBytesReceived;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get network bytes in");
            return 0;
        }
    }

    private double GetNetworkBytesOut()
    {
        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            var totalBytesSent = networkInterfaces
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Sum(ni => ni.GetIPStatistics().BytesSent);
            return totalBytesSent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get network bytes out");
            return 0;
        }
    }

    private int GetServiceCount()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Service");
                using var collection = searcher.Get();
                return collection.Count;
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get service count");
            return 0;
        }
    }

    private double GetSystemUptime()
    {
        try
        {
            return Environment.TickCount64 / 1000.0 / 60.0; // Convert to minutes
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get system uptime");
            return 0;
        }
    }
}
