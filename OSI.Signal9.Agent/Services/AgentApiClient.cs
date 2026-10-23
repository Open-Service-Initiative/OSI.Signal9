using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// The agent's view of the Signal9 REST API.
/// </summary>
// TODO(auth): attach the agent credential issued at enrollment once the API requires it.
public sealed class AgentApiClient(HttpClient http)
{
    public async Task<AgentDto> RegisterAsync(RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/v1/agents", request, Signal9Json.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, cancellationToken))!;
    }

    /// <returns>False when the API no longer knows this agent, or it was replaced, and it must re-register.</returns>
    public async Task<bool> SendHeartbeatAsync(Guid agentId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/agents/{agentId}/heartbeat", request, Signal9Json.Options, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task SendTelemetryAsync(Guid agentId, CreateTelemetrySampleRequest sample, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/agents/{agentId}/telemetry", sample, Signal9Json.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    // TODO(#103): list pending work from the agent's own URL once the doorbell lands.
    public async Task<IReadOnlyList<CommandDto>> GetPendingCommandsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var page = await http.GetFromJsonAsync<PagedResult<CommandDto>>(
            $"api/v1/devices/{deviceId}/commands?status={CommandStatus.Pending}&pageSize=100", Signal9Json.Options, cancellationToken);
        return page?.Items ?? [];
    }

    public async Task SendCommandResultAsync(CommandDto command, CommandResultRequest result, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/devices/{command.DeviceId}/commands/{command.Id}/result", result, Signal9Json.Options, cancellationToken);
        // 409 means the command was cancelled or already reported; nothing more to do.
        if (response.StatusCode != HttpStatusCode.Conflict)
            await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }
}
