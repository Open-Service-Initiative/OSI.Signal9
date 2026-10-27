using System.Diagnostics;
using OSI.Signal9.Agent.Platforms;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Agent.Services;

/// <summary>Where the agent's telemetry samples come from: the real machine, or a fake in unit tests.</summary>
public interface ITelemetrySource
{
    /// <summary>Takes a machine-wide telemetry sample.</summary>
    CreateTelemetrySampleRequest Collect();
}

/// <summary>
/// Takes a machine-wide telemetry sample: CPU and memory from the <see cref="IPlatform"/>, the rest from .NET on every
/// OS. CPU usage is averaged since the previous sample.
/// </summary>
public sealed class TelemetryCollector(IPlatform platform, TimeProvider time, ILogger<TelemetryCollector> logger) : ITelemetrySource
{
    private CpuTimes? _previousCpu = platform.ReadCpuTimes();

    public CreateTelemetrySampleRequest Collect()
    {
        var memory = Try(platform.ReadMemory, "memory");
        return new CreateTelemetrySampleRequest
        {
            CollectedAt = time.GetUtcNow(),
            CpuPercent = Try(ReadCpuPercent, "CPU"),
            MemoryTotalMb = memory?.TotalMb,
            MemoryUsedMb = memory is { } m ? m.TotalMb - m.AvailableMb : null,
            ProcessCount = Try(CountProcesses, "processes"),
            UptimeSeconds = Environment.TickCount64 / 1000,
            Disks = Try(ReadDisks, "disks") ?? [],
        };
    }

    private double? ReadCpuPercent()
    {
        var current = platform.ReadCpuTimes();
        var previous = _previousCpu;
        _previousCpu = current;
        if (current is not { } now || previous is not { } before || now.Total <= before.Total)
            return null;

        var busy = 1.0 - (double)(now.Idle - before.Idle) / (now.Total - before.Total);
        return Math.Round(Math.Clamp(busy * 100, 0, 100), 1);
    }

    private static int? CountProcesses()
    {
        var processes = Process.GetProcesses();
        foreach (var process in processes)
            process.Dispose();
        return processes.Length;
    }

    private static IReadOnlyList<DiskUsageDto> ReadDisks() => DriveInfo.GetDrives()
        .Where(d => d is { IsReady: true, DriveType: DriveType.Fixed } && d.TotalSize > 0)
        .Select(d => new DiskUsageDto(d.Name, d.TotalSize, d.AvailableFreeSpace))
        .ToList();

    private T? Try<T>(Func<T?> read, string metric)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read {Metric} telemetry", metric);
            return default;
        }
    }
}
