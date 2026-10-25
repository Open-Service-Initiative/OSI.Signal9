using OSI.Signal9.API.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Server.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
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
    CallerAudit audit,
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

    /// <summary>
    /// Queues a command and rings the doorbell of the device's active agent, which then fetches it. An agent awaiting
    /// approval hears nothing until it's approved.
    /// </summary>
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

        var device = await db.Devices.AsNoTracking().Include(d => d.ActiveAgent)
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
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
        // The command's type only: its parameters may name things on the device.
        Audit(AuditActions.CommandCreated, AuditOutcome.Succeeded, device, command, command.Type.ToString());
        await db.SaveChangesAsync(cancellationToken);

        var dto = command.ToDto();
        if (device.ActiveAgent is { } agent && agent.Status() == Contracts.Agents.AgentStatus.Active)
            await agents.Clients.Group(AgentHub.GroupFor(agent.Id)).WorkAvailable();
        await dashboard.Clients.All.CommandChanged(dto);
        this.SetETag(command.ETag());
        return CreatedAtRoute(nameof(GetCommand), new { deviceId, commandId = command.Id }, dto);
    }

    /// <summary>
    /// Records the outcome of a command. Sent once, when it finishes, by the agent the access token names, which must be
    /// the active agent of the command's device; any other agent gets 404. An agent awaiting approval receives no work,
    /// so it can't report any (409).
    /// </summary>
    [RequireScope(Scopes.Agent)]
    [HttpPut("{commandId:guid}/result")]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommandDto>> PutResult(
        Guid deviceId, Guid commandId, CommandResultRequest request, CancellationToken cancellationToken)
    {
        if (User.AgentId() is not { } agentId)
            return Forbid();
        return await CompleteAsync(deviceId, commandId, TenantScopeBypass.AgentSelfService, cancellationToken, (command, device) =>
        {
            // The resulting status only, never the output or error, which can hold anything from the device.
            Audit(AuditActions.CommandResult, request.Succeeded ? AuditOutcome.Succeeded : AuditOutcome.Failed, device, command,
                request.Succeeded ? nameof(CommandStatus.Completed) : nameof(CommandStatus.Failed));
            command.Status = request.Succeeded ? CommandStatus.Completed : CommandStatus.Failed;
            command.Output = request.Output;
            command.Error = request.Error;
        }, onlyAgent: agentId);
    }

    /// <summary>Cancels a pending command. Commands that already finished can't be cancelled (409).</summary>
    [RequireScope(Scopes.Commands.Execute)]
    [HttpPost("{commandId:guid}:cancel")]
    [OptimisticConcurrency]
    [ProducesResponseType<CommandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommandDto>> Cancel(Guid deviceId, Guid commandId, CancellationToken cancellationToken) =>
        CompleteAsync(deviceId, commandId, bypass: null, cancellationToken, (command, device) =>
        {
            Audit(AuditActions.CommandCancelled, AuditOutcome.Succeeded, device, command, detail: null);
            command.Status = CommandStatus.Cancelled;
        });

    /// <param name="onlyAgent">If given, the agent that must be the device's active agent; for any other, 404.</param>
    private async Task<ActionResult<CommandDto>> CompleteAsync(
        Guid deviceId, Guid commandId, TenantScopeBypass? bypass, CancellationToken cancellationToken,
        Action<Command, Device> complete, Guid? onlyAgent = null)
    {
        IQueryable<Command> commands = db.Commands;
        IQueryable<Device> devices = db.Devices.AsNoTracking().Include(d => d.ActiveAgent);
        if (bypass is { } reason)
        {
            commands = commands.IgnoreTenantScope(reason);
            devices = devices.IgnoreTenantScope(reason);
        }
        var command = await commands.SingleOrDefaultAsync(c => c.DeviceId == deviceId && c.Id == commandId, cancellationToken);
        if (command is null)
            return NotFound();
        var device = await devices.SingleAsync(d => d.Id == deviceId, cancellationToken);
        if (onlyAgent is { } agentId && device.ActiveAgentId != agentId)
            return NotFound();
        if (onlyAgent is not null && device.ActiveAgent!.Status() != Contracts.Agents.AgentStatus.Active)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Agent awaiting approval",
                detail: "The agent receives no work until a person approves it.");
        if (this.Preconditions(command.ETag()) is { } refused)
            return refused;
        if (command.Status != CommandStatus.Pending)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Command already finished",
                detail: $"The command is {command.Status}.");

        complete(command, device);
        command.CompletedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var dto = command.ToDto();
        await dashboard.Clients.All.CommandChanged(dto);
        this.SetETag(command.ETag());
        return dto;
    }

    /// <summary>Records <paramref name="action"/> on <paramref name="command"/> at the device's tenant, saved with it.</summary>
    private void Audit(string action, AuditOutcome outcome, Device device, Command command, string? detail) =>
        audit.Record(new AuditEntry
        {
            Action = action,
            Outcome = outcome,
            TenantId = device.TenantId,
            TargetType = "command",
            TargetId = command.Id,
            DeviceId = device.Id,
            Detail = detail,
        });
}
