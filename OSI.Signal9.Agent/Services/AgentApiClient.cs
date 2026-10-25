using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// The agent's view of the Signal9 REST API, as one enrolled agent: <paramref name="http"/> presents its certificate
/// and access token, which is where the API takes its identity from.
/// </summary>
public sealed class AgentApiClient(HttpClient http)
{
    /// <summary>Fails when the API refuses the agent, such as one that was replaced or is unknown.</summary>
    public async Task<HeartbeatResponse> SendHeartbeatAsync(Guid agentId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/agents/{agentId}/heartbeat", request, Signal9Json.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(Signal9Json.Options, cancellationToken))!;
    }

    public async Task SendTelemetryAsync(Guid agentId, CreateTelemetrySampleRequest sample, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/agents/{agentId}/telemetry", sample, Signal9Json.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Lists the agent's pending commands, oldest first, up to one page.</summary>
    public async Task<IReadOnlyList<CommandDto>> GetPendingCommandsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var page = await http.GetFromJsonAsync<PagedResult<CommandDto>>(
            $"api/v1/agents/{agentId}/pending-commands?pageSize=100", Signal9Json.Options, cancellationToken);
        return page?.Items ?? [];
    }

    /// <summary>Reports a command's result at its canonical URL.</summary>
    public async Task SendCommandResultAsync(CommandDto command, CommandResultRequest result, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"{command.Url.TrimStart('/')}/result", result, Signal9Json.Options, cancellationToken);
        // 409 means the command was cancelled or already reported; nothing more to do.
        if (response.StatusCode != HttpStatusCode.Conflict)
            await EnsureSuccessAsync(response, cancellationToken);
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }
}
