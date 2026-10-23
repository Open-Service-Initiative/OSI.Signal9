using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Agents;
using OSI.Signal9.API.Data;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.API.Telemetry;

public sealed record TelemetryQuery : PageQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

[ApiController]
[Route("api/v1")]
public sealed class TelemetryController(
    Signal9DbContext db,
    TimeProvider time,
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    /// <summary>Lists a device's telemetry samples, newest first, across all the agents it has had.</summary>
    [HttpGet("devices/{deviceId:guid}/telemetry")]
    [ProducesResponseType<PagedResult<TelemetrySampleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<TelemetrySampleDto>>> List(
        Guid deviceId, [FromQuery] TelemetryQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == deviceId, cancellationToken))
            return NotFound();

        var samples = db.TelemetrySamples.AsNoTracking().Where(s => s.DeviceId == deviceId);
        if (query.From is { } from)
            samples = samples.Where(s => s.CollectedAt >= from.UtcDateTime);
        if (query.To is { } to)
            samples = samples.Where(s => s.CollectedAt < to.UtcDateTime);

        return await samples
            .OrderByDescending(s => s.CollectedAt)
            .ToPagedResultAsync(query, s => s.ToDto(), cancellationToken);
    }

    [HttpGet("devices/{deviceId:guid}/telemetry/{sampleId:guid}", Name = nameof(GetSample))]
    [ProducesResponseType<TelemetrySampleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetrySampleDto>> GetSample(Guid deviceId, Guid sampleId, CancellationToken cancellationToken)
    {
        var sample = await db.TelemetrySamples.AsNoTracking()
            .SingleOrDefaultAsync(s => s.DeviceId == deviceId && s.Id == sampleId, cancellationToken);
        return sample is null ? NotFound() : sample.ToDto();
    }

    /// <summary>
    /// Stores a telemetry sample sent by an agent on its device. Also counts as a heartbeat.
    /// The sample's canonical URL is under the device.
    /// </summary>
    [HttpPost("agents/{agentId:guid}/telemetry")]
    [Consumes("application/json")]
    [ProducesResponseType<TelemetrySampleDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TelemetrySampleDto>> Create(
        Guid agentId, CreateTelemetrySampleRequest request, CancellationToken cancellationToken)
    {
        var (device, refusal) = await db.FindDeviceOfActiveAgentAsync(this, agentId, cancellationToken);
        if (device is null)
            return (ActionResult)refusal!;

        var now = time.GetUtcNow().UtcDateTime;
        var sample = new TelemetrySample
        {
            Id = Guid.CreateVersion7(),
            DeviceId = device.Id,
            CollectedAt = request.CollectedAt.UtcDateTime,
            ReceivedAt = now,
            CpuPercent = request.CpuPercent,
            MemoryUsedMb = request.MemoryUsedMb,
            MemoryTotalMb = request.MemoryTotalMb,
            ProcessCount = request.ProcessCount,
            UptimeSeconds = request.UptimeSeconds,
            Disks = request.Disks.Select(d => new DiskUsage { Name = d.Name, TotalBytes = d.TotalBytes, FreeBytes = d.FreeBytes }).ToList(),
        };
        db.TelemetrySamples.Add(sample);
        device.ActiveAgent!.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var dto = sample.ToDto();
        await dashboard.Clients.All.TelemetryReceived(dto);
        return CreatedAtRoute(nameof(GetSample), new { deviceId = device.Id, sampleId = sample.Id }, dto);
    }
}
