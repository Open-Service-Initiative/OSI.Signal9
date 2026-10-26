using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
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
        using var response = await http.PutAsJsonAsync(AgentProtocol.Routes.HeartbeatOf(agentId), request, AgentJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(AgentJson.Options, cancellationToken))!;
    }

    /// <summary>
    /// Sends a telemetry sample. A 409 drops it: the agent awaits an identification decision and has no device to file
    /// it under yet (#131). A replaced agent gets 409 too, but its heartbeat fails and ends the session.
    /// </summary>
    public async Task SendTelemetryAsync(Guid agentId, CreateTelemetrySampleRequest sample, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(AgentProtocol.Routes.TelemetryOf(agentId), sample, AgentJson.Options, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Conflict)
            await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Lists the agent's pending commands, oldest first, up to one page.</summary>
    public async Task<IReadOnlyList<CommandDto>> GetPendingCommandsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var page = await http.GetFromJsonAsync<PagedResult<CommandDto>>(
            $"{AgentProtocol.Routes.PendingCommandsOf(agentId)}?pageSize=100", AgentJson.Options, cancellationToken);
        return page?.Items ?? [];
    }

    /// <summary>Reports a command's result at its canonical URL.</summary>
    public async Task SendCommandResultAsync(CommandDto command, CommandResultRequest result, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(AgentProtocol.Routes.CommandResultOf(command.Url), result, AgentJson.Options, cancellationToken);
        // 409 means the command was cancelled or already reported; nothing more to do.
        if (response.StatusCode != HttpStatusCode.Conflict)
            await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Replaces the agent's certificate with <paramref name="certificatePem"/>, its new key's (story 102). Once this
    /// succeeds, only the new certificate authenticates.
    /// </summary>
    public async Task PutCertificateAsync(Guid agentId, string certificatePem, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(AgentProtocol.Routes.CertificateOf(agentId),
            new AgentCertificateRequest { Certificate = certificatePem }, AgentJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <exception cref="UnsupportedAgentVersionException">api refused the agent's version.</exception>
    /// <exception cref="HttpRequestException">api refused the request for another reason.</exception>
    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest && VersionRefusal(body) is { } refusal)
            throw refusal;
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }

    /// <summary>
    /// The refusal in <paramref name="body"/> if it refuses the agent's version: a problem of that type from api's own
    /// endpoints, or that OAuth error from the token endpoint.
    /// </summary>
    private static UnsupportedAgentVersionException? VersionRefusal(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            string? Member(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (Member("type") != AgentProtocol.UnsupportedVersion.ProblemType && Member("error") != AgentProtocol.UnsupportedVersion.Error)
                return null;
            return new UnsupportedAgentVersionException(
                Member("detail") ?? Member("error_description") ?? "api doesn't support this agent version.",
                Member("minimumVersion"), Member("maximumVersion"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
