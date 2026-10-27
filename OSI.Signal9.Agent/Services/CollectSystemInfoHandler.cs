using System.Text.Json;
using OSI.Signal9.Contracts.Commands;

namespace OSI.Signal9.Agent.Services;

/// <summary>Reports this machine's facts and a telemetry sample.</summary>
public sealed class CollectSystemInfoHandler(IMachineFactsSource systemInfo, ITelemetrySource telemetry) : ICommandHandler
{
    public CommandType Type => CommandType.CollectSystemInfo;

    public Task<CommandResultRequest> HandleAsync(CommandDto command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new { System = systemInfo.GetFacts(), Telemetry = telemetry.Collect() };
        return Task.FromResult(new CommandResultRequest
        {
            Succeeded = true,
            Output = JsonSerializer.Serialize(output, AgentJson.Options),
        });
    }
}
