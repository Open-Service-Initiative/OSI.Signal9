using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Server.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
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
    /// <summary>Lists a device's commands, newest first. Agents fetch their work from their own pending-commands list.</summary>
    [RequireScope(Scopes.Commands.Read)]
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

    [RequireScope(Scopes.Commands.Read)]
    [HttpGet("{commandId:guid}", Name = nameof(GetCommand))]
    [OptimisticConcurrency]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommandDto>> GetCommand(Guid deviceId, Guid commandId, CancellationToken cancellationToken)
    {
        var command = await db.Commands.AsNoTracking()
            .SingleOrDefaultAsync(c => c.DeviceId == deviceId && c.Id == commandId, cancellationToken);
        return command is null ? NotFound() : this.Conditional(command.ToDto(), command.ETag());
    }

    /// <summary>Queues a command and rings the doorbell of the device's active agent, which then fetches it.</summary>
    [RequireScope(Scopes.Commands.Execute)]
    [EndpointDescription($"{nameof(CommandType.RunScript)} runs ad-hoc code, so it also requires {Scopes.Terminal.Execute}.")]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<CommandDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommandDto>> Create(
        Guid deviceId, CreateCommandRequest request, [FromServices] IAuthorizationService authorization,
        [FromServices] CallerAccess access, CancellationToken cancellationToken)
    {
        // TODO(#82): ad-hoc code moves to script executions, and RunScript leaves the command contract.
        var terminal = new RequireScopeAttribute(Scopes.Terminal.Execute);
        if (request.Type == CommandType.RunScript)
        {
            if (!(await authorization.AuthorizeAsync(User, null, terminal)).Succeeded)
                return InsufficientScope.Forbid(Scopes.Terminal.Execute);
            // The role must grant it at the device's tenant too.
            access.Require(Scopes.Terminal.Execute);
        }

        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
            return NotFound();
        if (this.Preconditions(current: null) is { } refused)
            return refused;

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
            await agents.Clients.Group(AgentHub.GroupFor(agentId)).WorkAvailable();
        await dashboard.Clients.All.CommandChanged(dto);
        this.SetETag(command.ETag());
        return CreatedAtRoute(nameof(GetCommand), new { deviceId, commandId = command.Id }, dto);
    }

    /// <summary>Records the outcome of a command. Sent by the agent once, when it finishes.</summary>
    [RequireScope(Scopes.Agent)]
    [HttpPut("{commandId:guid}/result")]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommandDto>> PutResult(
        Guid deviceId, Guid commandId, CommandResultRequest request, CancellationToken cancellationToken) =>
        CompleteAsync(deviceId, commandId, TenantScopeBypass.AgentSelfService, cancellationToken, command =>
        {
            command.Status = request.Succeeded ? CommandStatus.Completed : CommandStatus.Failed;
            command.Output = request.Output;
            command.Error = request.Error;
        });

    /// <summary>Cancels a pending command. Commands that already finished can't be cancelled (409).</summary>
    [RequireScope(Scopes.Commands.Execute)]
    [HttpPost("{commandId:guid}:cancel")]
    [OptimisticConcurrency]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommandDto>> Cancel(Guid deviceId, Guid commandId, CancellationToken cancellationToken) =>
        CompleteAsync(deviceId, commandId, bypass: null, cancellationToken, command => command.Status = CommandStatus.Cancelled);

    private async Task<ActionResult<CommandDto>> CompleteAsync(
        Guid deviceId, Guid commandId, TenantScopeBypass? bypass, CancellationToken cancellationToken, Action<Command> complete)
    {
        IQueryable<Command> commands = db.Commands;
        if (bypass is { } reason)
            commands = commands.IgnoreTenantScope(reason);
        var command = await commands.SingleOrDefaultAsync(c => c.DeviceId == deviceId && c.Id == commandId, cancellationToken);
        if (command is null)
            return NotFound();
        if (this.Preconditions(command.ETag()) is { } refused)
            return refused;
        if (command.Status != CommandStatus.Pending)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Command already finished",
                detail: $"The command is {command.Status}.");

        complete(command);
        command.CompletedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var dto = command.ToDto();
        await dashboard.Clients.All.CommandChanged(dto);
        this.SetETag(command.ETag());
        return dto;
    }
}
