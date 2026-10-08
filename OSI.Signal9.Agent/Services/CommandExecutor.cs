using System.Text.Json;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Executes commands sent from the dashboard. Unsupported commands fail explicitly instead of pretending to succeed.
/// </summary>
public sealed class CommandExecutor(SystemInfoProvider systemInfo, TelemetryCollector telemetry, ILogger<CommandExecutor> logger)
{
    public async Task<CommandResultRequest> ExecuteAsync(AgentCommandDto command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Executing {CommandType} command {CommandId}", command.Type, command.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));

        try
        {
            return command.Type switch
            {
                CommandType.CollectSystemInfo => await CollectSystemInfoAsync(timeout.Token),
                // TODO: RestartService, RunScript and RestartMachine need a signed, allow-listed design before they exist.
                _ => new CommandResultRequest { Succeeded = false, Error = $"{command.Type} is not supported by this agent version." },
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandResultRequest { Succeeded = false, Error = $"Timed out after {command.TimeoutSeconds} seconds." };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command {CommandId} failed", command.Id);
            return new CommandResultRequest { Succeeded = false, Error = ex.Message };
        }
    }

    private Task<CommandResultRequest> CollectSystemInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new { Registration = systemInfo.GetRegistration(), Telemetry = telemetry.Collect() };
        return Task.FromResult(new CommandResultRequest
        {
            Succeeded = true,
            Output = JsonSerializer.Serialize(output, Signal9Json.Options),
        });
    }
}
