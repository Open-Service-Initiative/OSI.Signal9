using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.API.Devices;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Agents;

/// <summary>
/// Agents: installations of the agent software, each on one device.
/// </summary>
// TODO(auth): Register and heartbeat are called by agents, GetAgent by people; they need different policies.
[ApiController]
[Route("api/v1/agents")]
public sealed class AgentsController(
    Signal9DbContext db,
    DevicePresence presence,
    TimeProvider time,
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    [HttpGet("{agentId:guid}", Name = nameof(GetAgent))]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDto>> GetAgent(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Device)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        return agent is null ? NotFound() : agent.ToDto(agent.Device!);
    }

    /// <summary>
    /// Registers a new agent in the tenant identified by <see cref="RegisterAgentRequest.TenantId"/>. The agent becomes the
    /// active agent of the tenant's device with the same machine name, replacing its previous agent, or of a new device.
    /// </summary>
    // TODO(enrollment): replaced by enrollment tokens (#128) and identification (#131), which never match machine names.
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<AgentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AgentDto>> Register(RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            ModelState.AddModelError(nameof(request.TenantId), "No active tenant has this ID.");
            return ValidationProblem(ModelState);
        }

        // A new device and its first agent point at each other, so they're saved in two steps, in one transaction.
        var strategy = db.Database.CreateExecutionStrategy();
        var registered = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await RegisterAsync(tenant, request, cancellationToken);
            if (result.Value is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        });
        if (registered.Value is not ({ } device, { } agent))
            return registered.Problem!;

        await dashboard.Clients.All.DeviceChanged(device.ToDto(presence));
        return CreatedAtRoute(nameof(GetAgent), new { agentId = agent.Id }, agent.ToDto(device));
    }

    private async Task<(ActionResult? Problem, (Device, Agent)? Value)> RegisterAsync(
        Tenant tenant, RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var device = await db.Devices
            .Where(d => d.TenantId == tenant.Id && d.MachineName == request.MachineName)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
        {
            var devices = await db.Devices.CountAsync(d => d.TenantId == tenant.Id, cancellationToken);
            if (devices >= tenant.MaxDevices)
                return (Problem(statusCode: StatusCodes.Status409Conflict, title: "Device limit reached",
                    detail: $"Tenant '{tenant.Name}' allows at most {tenant.MaxDevices} devices."), null);

            device = new Device
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                MachineName = request.MachineName,
                OperatingSystem = request.OperatingSystem,
                CreatedAt = now,
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync(cancellationToken);
        }

        device.Domain = request.Domain;
        device.OperatingSystem = request.OperatingSystem;
        device.OsVersion = request.OsVersion;
        device.Architecture = request.Architecture;
        device.ProcessorName = request.ProcessorName;
        device.ProcessorCores = request.ProcessorCores;
        device.TotalMemoryMb = request.TotalMemoryMb;
        device.IpAddress = request.IpAddress;
        device.MacAddress = request.MacAddress;

        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            DeviceId = device.Id,
            AgentVersion = request.AgentVersion,
            EnrolledAt = now,
            LastSeenAt = now,
        };
        db.Agents.Add(agent);
        device.ActiveAgent = agent;
        await db.SaveChangesAsync(cancellationToken);
        return (null, (device, agent));
    }

    /// <summary>
    /// Lists the pending commands of the agent's device, oldest first. The agent calls it when its doorbell rings, every
    /// time it (re)connects and when a heartbeat says work is waiting, then reports each result at the command's URL.
    /// A replaced agent gets 409.
    /// </summary>
    [HttpGet("{agentId:guid}/pending-commands")]
    [ProducesResponseType<PagedResult<CommandDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PagedResult<CommandDto>>> ListPendingCommands(
        Guid agentId, [FromQuery] PageQuery query, CancellationToken cancellationToken)
    {
        var (device, refusal) = await db.FindDeviceOfActiveAgentAsync(this, agentId, cancellationToken);
        if (device is null)
            return (ActionResult)refusal!;

        return await db.Commands.AsNoTracking()
            .Where(c => c.DeviceId == device.Id && c.Status == CommandStatus.Pending)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToPagedResultAsync(query, c => c.ToDto(), cancellationToken);
    }

    /// <summary>
    /// Records that the agent is alive and tells it whether work is waiting. Sent by the agent on a fixed interval.
    /// A replaced agent gets 409.
    /// </summary>
    [HttpPut("{agentId:guid}/heartbeat")]
    [Consumes("application/json")]
    [ProducesResponseType<HeartbeatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HeartbeatResponse>> Heartbeat(
        Guid agentId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var (device, refusal) = await db.FindDeviceOfActiveAgentAsync(this, agentId, cancellationToken);
        if (device is null)
            return (ActionResult)refusal!;

        var agent = device.ActiveAgent!;
        var wasOnline = presence.StatusOf(device) == Contracts.Devices.DeviceStatus.Online;
        agent.LastSeenAt = time.GetUtcNow().UtcDateTime;
        agent.AgentVersion = request.AgentVersion ?? agent.AgentVersion;
        device.IpAddress = request.IpAddress ?? device.IpAddress;
        await db.SaveChangesAsync(cancellationToken);

        if (!wasOnline)
            await dashboard.Clients.All.DeviceChanged(device.ToDto(presence));
        var hasPendingWork = await db.Commands.AnyAsync(
            c => c.DeviceId == device.Id && c.Status == CommandStatus.Pending, cancellationToken);
        return new HeartbeatResponse(hasPendingWork);
    }
}
