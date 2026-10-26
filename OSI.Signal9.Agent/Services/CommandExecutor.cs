using OSI.Signal9.Contracts.Commands;

namespace OSI.Signal9.Agent.Services;

/// <summary>Runs one type of command.</summary>
public interface ICommandHandler
{
    CommandType Type { get; }

    /// <summary>Runs <paramref name="command"/>. Cancellation means the agent is stopping or the command timed out.</summary>
    Task<CommandResultRequest> HandleAsync(CommandDto command, CancellationToken cancellationToken);
}

/// <summary>
/// Executes commands sent from the dashboard within their timeout. Unsupported commands fail explicitly instead of
/// pretending to succeed.
/// </summary>
public sealed class CommandExecutor(IEnumerable<ICommandHandler> handlers, TimeProvider time, ILogger<CommandExecutor> logger)
{
    // TODO: RestartService, RunScript and RestartMachine need a signed, allow-listed design before they have handlers.
    private readonly Dictionary<CommandType, ICommandHandler> _handlers = handlers.ToDictionary(h => h.Type);

    public async Task<CommandResultRequest> ExecuteAsync(CommandDto command, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(command.Type, out var handler))
            return new CommandResultRequest
            {
                Succeeded = false,
                // A type newer than this agent reads as an undefined value, with no name to give.
                Error = $"{(Enum.IsDefined(command.Type) ? command.Type : "This command type")} is not supported by this agent version.",
            };

        logger.LogInformation("Executing {CommandType} command {CommandId}", command.Type, command.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await handler.HandleAsync(command, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new CommandResultRequest { Succeeded = false, Error = $"Timed out after {command.TimeoutSeconds} seconds." };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Command {CommandId} failed", command.Id);
            return new CommandResultRequest { Succeeded = false, Error = ex.Message };
        }
    }
}
