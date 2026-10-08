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
/// Commands queued for an agent. A command starts Pending and ends Completed, Failed or Cancelled.
/// </summary>
[ApiController]
[Route("api/agents/{agentId:guid}/commands")]
public sealed class AgentCommandsController(
    Signal9DbContext db,
    TimeProvider time,
    IHubContext<AgentHub, IAgentHubClient> agents,
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    /// <summary>Lists an agent's commands, newest first. Agents poll ?status=Pending after reconnecting.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<AgentCommandDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<AgentCommandDto>>> List(
        Guid agentId, [FromQuery] CommandQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Agents.AnyAsync(a => a.Id == agentId, cancellationToken))
            return NotFound();

        var commands = db.AgentCommands.AsNoTracking().Where(c => c.AgentId == agentId);
        if (query.Status is { } status)
            commands = commands.Where(c => c.Status == status);

        return await commands
            .OrderByDescending(c => c.CreatedAt)
            .ToPagedResultAsync(query, c => c.ToDto(), cancellationToken);
    }

    [HttpGet("{commandId:guid}", Name = nameof(GetCommand))]
    [ProducesResponseType<AgentCommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentCommandDto>> GetCommand(Guid agentId, Guid commandId, CancellationToken cancellationToken)
    {
        var command = await FindAsync(agentId, commandId, tracking: false, cancellationToken);
        return command is null ? NotFound() : command.ToDto();
    }

    /// <summary>Queues a command and pushes it to the agent if it is connected.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<AgentCommandDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentCommandDto>> Create(
        Guid agentId, CreateAgentCommandRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Agents.AnyAsync(a => a.Id == agentId, cancellationToken))
            return NotFound();

        var command = new AgentCommand
        {
            Id = Guid.CreateVersion7(),
            AgentId = agentId,
            Type = request.Type,
            Parameters = request.Parameters,
            Priority = request.Priority,
            TimeoutSeconds = request.TimeoutSeconds,
            Status = CommandStatus.Pending,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.AgentCommands.Add(command);
        await db.SaveChangesAsync(cancellationToken);

        var dto = command.ToDto();
        await agents.Clients.Group(AgentHub.GroupFor(agentId)).CommandQueued(dto);
        await dashboard.Clients.All.CommandChanged(dto);
        return CreatedAtRoute(nameof(GetCommand), new { agentId, commandId = command.Id }, dto);
    }

    /// <summary>Records the outcome of a command. Sent by the agent once, when it finishes.</summary>
    [HttpPut("{commandId:guid}/result")]
    [Consumes("application/json")]
    [ProducesResponseType<AgentCommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<AgentCommandDto>> PutResult(
        Guid agentId, Guid commandId, CommandResultRequest request, CancellationToken cancellationToken) =>
        CompleteAsync(agentId, commandId, cancellationToken, command =>
        {
            command.Status = request.Succeeded ? CommandStatus.Completed : CommandStatus.Failed;
            command.Output = request.Output;
            command.Error = request.Error;
        });

    /// <summary>Cancels a pending command. Commands that already finished cannot be cancelled.</summary>
    [HttpPut("{commandId:guid}/cancellation")]
    [ProducesResponseType<AgentCommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<AgentCommandDto>> Cancel(Guid agentId, Guid commandId, CancellationToken cancellationToken) =>
        CompleteAsync(agentId, commandId, cancellationToken, command => command.Status = CommandStatus.Cancelled);

    private async Task<ActionResult<AgentCommandDto>> CompleteAsync(
        Guid agentId, Guid commandId, CancellationToken cancellationToken, Action<AgentCommand> complete)
    {
        var command = await FindAsync(agentId, commandId, tracking: true, cancellationToken);
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

    private Task<AgentCommand?> FindAsync(Guid agentId, Guid commandId, bool tracking, CancellationToken cancellationToken)
    {
        var commands = tracking ? db.AgentCommands : db.AgentCommands.AsNoTracking();
        return commands.SingleOrDefaultAsync(c => c.AgentId == agentId && c.Id == commandId, cancellationToken);
    }
}
