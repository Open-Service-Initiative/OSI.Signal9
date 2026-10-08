using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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
[Route("api/agents/{agentId:guid}/telemetry")]
public sealed class TelemetryController(
    Signal9DbContext db,
    TimeProvider time,
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    /// <summary>Lists an agent's telemetry samples, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<TelemetrySampleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<TelemetrySampleDto>>> List(
        Guid agentId, [FromQuery] TelemetryQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Agents.AnyAsync(a => a.Id == agentId, cancellationToken))
            return NotFound();

        var samples = db.TelemetrySamples.AsNoTracking().Where(s => s.AgentId == agentId);
        if (query.From is { } from)
            samples = samples.Where(s => s.CollectedAt >= from.UtcDateTime);
        if (query.To is { } to)
            samples = samples.Where(s => s.CollectedAt < to.UtcDateTime);

        return await samples
            .OrderByDescending(s => s.CollectedAt)
            .ToPagedResultAsync(query, s => s.ToDto(), cancellationToken);
    }

    [HttpGet("{sampleId:guid}", Name = nameof(GetSample))]
    [ProducesResponseType<TelemetrySampleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetrySampleDto>> GetSample(Guid agentId, Guid sampleId, CancellationToken cancellationToken)
    {
        var sample = await db.TelemetrySamples.AsNoTracking()
            .SingleOrDefaultAsync(s => s.AgentId == agentId && s.Id == sampleId, cancellationToken);
        return sample is null ? NotFound() : sample.ToDto();
    }

    /// <summary>Stores a telemetry sample reported by the agent. Also counts as a heartbeat.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<TelemetrySampleDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetrySampleDto>> Create(
        Guid agentId, CreateTelemetrySampleRequest request, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return NotFound();

        var now = time.GetUtcNow().UtcDateTime;
        var sample = new TelemetrySample
        {
            Id = Guid.CreateVersion7(),
            AgentId = agentId,
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
        agent.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var dto = sample.ToDto();
        await dashboard.Clients.All.TelemetryReceived(dto);
        return CreatedAtRoute(nameof(GetSample), new { agentId, sampleId = sample.Id }, dto);
    }
}
