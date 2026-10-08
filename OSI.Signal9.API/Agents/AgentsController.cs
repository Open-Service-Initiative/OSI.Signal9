using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Data;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Agents;

// TODO(auth): POST and heartbeat are called by agents, the rest by dashboard users; they need different policies.
[ApiController]
[Route("api/agents")]
public sealed class AgentsController(
    Signal9DbContext db,
    AgentPresence presence,
    TimeProvider time,
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    /// <summary>Lists agents across tenants, with optional filters.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<AgentDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<AgentDto>> List(
        [FromQuery] PageQuery paging,
        [FromQuery] Guid? tenantId,
        [FromQuery] AgentStatus? status,
        [FromQuery] string? group,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var query = db.Agents.AsNoTracking();
        if (tenantId is not null)
            query = query.Where(a => a.TenantId == tenantId);
        if (status is not null)
            query = presence.WhereStatus(query, status.Value);
        if (!string.IsNullOrWhiteSpace(group))
            query = query.Where(a => a.GroupName == group);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => a.MachineName.Contains(search) || (a.IpAddress != null && a.IpAddress.Contains(search)));

        return await query
            .OrderBy(a => a.MachineName)
            .ToPagedResultAsync(paging, a => a.ToDto(presence), cancellationToken);
    }

    [HttpGet("{agentId:guid}", Name = nameof(GetAgent))]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDto>> GetAgent(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        return agent is null ? NotFound() : agent.ToDto(presence);
    }

    /// <summary>
    /// Enrolls an agent in the tenant identified by <see cref="RegisterAgentRequest.TenantCode"/>.
    /// Returns 201 for a new agent, or 200 when the machine was already enrolled and its facts were refreshed.
    /// </summary>
    // TODO(auth): replace the tenant code with a per-tenant enrollment secret and issue the agent a credential.
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<AgentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AgentDto>> Register(RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Code == request.TenantCode, cancellationToken);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            ModelState.AddModelError(nameof(request.TenantCode), "No active tenant has this code.");
            return ValidationProblem(ModelState);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var agent = await db.Agents.SingleOrDefaultAsync(
            a => a.TenantId == tenant.Id && a.MachineName == request.MachineName, cancellationToken);
        var isNew = agent is null;

        if (agent is null)
        {
            var enrolled = await db.Agents.CountAsync(a => a.TenantId == tenant.Id, cancellationToken);
            if (enrolled >= tenant.MaxAgents)
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Agent limit reached",
                    detail: $"Tenant '{tenant.Code}' allows at most {tenant.MaxAgents} agents.");

            agent = new Agent
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                MachineName = request.MachineName,
                OperatingSystem = request.OperatingSystem,
                FirstSeenAt = now,
            };
            db.Agents.Add(agent);
        }

        agent.Domain = request.Domain;
        agent.OperatingSystem = request.OperatingSystem;
        agent.OsVersion = request.OsVersion;
        agent.Architecture = request.Architecture;
        agent.ProcessorName = request.ProcessorName;
        agent.ProcessorCores = request.ProcessorCores;
        agent.TotalMemoryMb = request.TotalMemoryMb;
        agent.IpAddress = request.IpAddress;
        agent.MacAddress = request.MacAddress;
        agent.AgentVersion = request.AgentVersion;
        agent.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var dto = agent.ToDto(presence);
        await dashboard.Clients.All.AgentChanged(dto);
        return isNew ? CreatedAtRoute(nameof(GetAgent), new { agentId = agent.Id }, dto) : Ok(dto);
    }

    /// <summary>Replaces the admin-managed fields of an agent (group, tags, maintenance flag).</summary>
    [HttpPut("{agentId:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDto>> Update(Guid agentId, UpdateAgentRequest request, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return NotFound();

        agent.GroupName = string.IsNullOrWhiteSpace(request.GroupName) ? null : request.GroupName.Trim();
        agent.Tags = request.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        agent.InMaintenance = request.InMaintenance;
        await db.SaveChangesAsync(cancellationToken);

        var dto = agent.ToDto(presence);
        await dashboard.Clients.All.AgentChanged(dto);
        return dto;
    }

    /// <summary>Removes an agent along with its commands and telemetry.</summary>
    [HttpDelete("{agentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid agentId, CancellationToken cancellationToken)
    {
        var deleted = await db.Agents.Where(a => a.Id == agentId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
            return NotFound();

        await dashboard.Clients.All.AgentDeleted(agentId);
        return NoContent();
    }

    /// <summary>Records that the agent is alive. Sent by the agent on a fixed interval.</summary>
    [HttpPut("{agentId:guid}/heartbeat")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Heartbeat(Guid agentId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return NotFound();

        var wasOnline = presence.StatusOf(agent) == AgentStatus.Online;
        agent.LastSeenAt = time.GetUtcNow().UtcDateTime;
        agent.IpAddress = request.IpAddress ?? agent.IpAddress;
        agent.AgentVersion = request.AgentVersion ?? agent.AgentVersion;
        await db.SaveChangesAsync(cancellationToken);

        if (!wasOnline)
            await dashboard.Clients.All.AgentChanged(agent.ToDto(presence));
        return NoContent();
    }
}
