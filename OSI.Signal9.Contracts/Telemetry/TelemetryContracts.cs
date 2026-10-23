using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Telemetry;

public sealed record DiskUsageDto(
    [Required, StringLength(64)] string Name,
    [Range(0, long.MaxValue)] long TotalBytes,
    [Range(0, long.MaxValue)] long FreeBytes);

public sealed record TelemetrySampleDto(
    Guid Id,
    Guid AgentId,
    DateTimeOffset CollectedAt,
    double? CpuPercent,
    long? MemoryUsedMb,
    long? MemoryTotalMb,
    int? ProcessCount,
    long? UptimeSeconds,
    IReadOnlyList<DiskUsageDto> Disks);

public sealed record CreateTelemetrySampleRequest
{
    [Required]
    public DateTimeOffset CollectedAt { get; init; }

    [Range(0, 100)]
    public double? CpuPercent { get; init; }

    [Range(0, long.MaxValue)]
    public long? MemoryUsedMb { get; init; }

    [Range(0, long.MaxValue)]
    public long? MemoryTotalMb { get; init; }

    [Range(0, int.MaxValue)]
    public int? ProcessCount { get; init; }

    [Range(0, long.MaxValue)]
    public long? UptimeSeconds { get; init; }

    [MaxLength(64)]
    public IReadOnlyList<DiskUsageDto> Disks { get; init; } = [];
}
