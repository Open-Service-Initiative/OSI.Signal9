using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Data;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;

namespace OSI.Signal9.API.Commands;

public sealed record CommandQuery : PageQuery
{
    public CommandStatus? Status { get; init; }
}

/// <summary>
/// Commands for a device, run by its active agent. A command starts Pending and ends Completed, Failed or Cancelled,
/// and stays in the device's history when the agent is replaced.
/// </summary>
[ApiController]
[Route("api/v1/devices/{deviceId:guid}/commands")]
public sealed class CommandsController(
    Signal9DbContext db,
    TimeProvider time,
    IHubContext<AgentHub, IAgentHubClient> agents,
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    /// <summary>Lists a device's commands, newest first. Its agent fetches ?status=Pending after reconnecting.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<CommandDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<CommandDto>>> List(
        Guid deviceId, [FromQuery] CommandQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == deviceId, cancellationToken))
            return NotFound();

        var commands = db.Commands.AsNoTracking().Where(c => c.DeviceId == deviceId);
        if (query.Status is { } status)
            commands = commands.Where(c => c.Status == status);

        return await commands
            .OrderByDescending(c => c.CreatedAt)
            .ToPagedResultAsync(query, c => c.ToDto(), cancellationToken);
    }

    [HttpGet("{commandId:guid}", Name = nameof(GetCommand))]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommandDto>> GetCommand(Guid deviceId, Guid commandId, CancellationToken cancellationToken)
    {
        var command = await FindAsync(deviceId, commandId, tracking: false, cancellationToken);
        return command is null ? NotFound() : command.ToDto();
    }

    /// <summary>Queues a command and pushes it to the device's active agent if it is connected.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<CommandDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommandDto>> Create(
        Guid deviceId, CreateCommandRequest request, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
            return NotFound();

        var command = new Command
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            Type = request.Type,
            Parameters = request.Parameters,
            Priority = request.Priority,
            TimeoutSeconds = request.TimeoutSeconds,
            Status = CommandStatus.Pending,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Commands.Add(command);
        await db.SaveChangesAsync(cancellationToken);

        var dto = command.ToDto();
        if (device.ActiveAgentId is { } agentId)
            await agents.Clients.Group(AgentHub.GroupFor(agentId)).CommandQueued(dto);
        await dashboard.Clients.All.CommandChanged(dto);
        return CreatedAtRoute(nameof(GetCommand), new { deviceId, commandId = command.Id }, dto);
    }

    /// <summary>Records the outcome of a command. Sent by the agent once, when it finishes.</summary>
    [HttpPut("{commandId:guid}/result")]
    [Consumes("application/json")]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommandDto>> PutResult(
        Guid deviceId, Guid commandId, CommandResultRequest request, CancellationToken cancellationToken) =>
        CompleteAsync(deviceId, commandId, cancellationToken, command =>
        {
            command.Status = request.Succeeded ? CommandStatus.Completed : CommandStatus.Failed;
            command.Output = request.Output;
            command.Error = request.Error;
        });

    /// <summary>Cancels a pending command. Commands that already finished can't be cancelled (409).</summary>
    [HttpPost("{commandId:guid}:cancel")]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommandDto>> Cancel(Guid deviceId, Guid commandId, CancellationToken cancellationToken) =>
        CompleteAsync(deviceId, commandId, cancellationToken, command => command.Status = CommandStatus.Cancelled);

    private async Task<ActionResult<CommandDto>> CompleteAsync(
        Guid deviceId, Guid commandId, CancellationToken cancellationToken, Action<Command> complete)
    {
        var command = await FindAsync(deviceId, commandId, tracking: true, cancellationToken);
        if (command is null)
            return NotFound();
        if (command.Status != CommandStatus.Pending)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Command already finished",
                detail: $"The command is {command.Status}.");

        complete(command);
        command.CompletedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var dto = command.ToDto();
        await dashboard.Clients.All.CommandChanged(dto);
        return dto;
    }

    private Task<Command?> FindAsync(Guid deviceId, Guid commandId, bool tracking, CancellationToken cancellationToken)
    {
        var commands = tracking ? db.Commands : db.Commands.AsNoTracking();
        return commands.SingleOrDefaultAsync(c => c.DeviceId == deviceId && c.Id == commandId, cancellationToken);
    }
}
