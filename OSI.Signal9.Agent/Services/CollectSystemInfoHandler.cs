using System.Text.Json;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.Agent.Services;

/// <summary>Reports this machine's registration facts and a telemetry sample.</summary>
public sealed class CollectSystemInfoHandler(SystemInfoProvider systemInfo, TelemetryCollector telemetry) : ICommandHandler
{
    public CommandType Type => CommandType.CollectSystemInfo;

    public Task<CommandResultRequest> HandleAsync(CommandDto command, CancellationToken cancellationToken)
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
