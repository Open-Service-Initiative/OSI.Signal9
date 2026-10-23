using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.Web.Services;

/// <summary>
/// Typed client for the Signal9 REST API. Non-success responses throw <see cref="ApiException"/>.
/// </summary>
public sealed class Signal9ApiClient(HttpClient http)
{
    // Tenants

    public Task<PagedResult<TenantDto>> GetTenantsAsync(int page = 1, int pageSize = 100, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<TenantDto>>($"api/tenants?page={page}&pageSize={pageSize}", cancellationToken);

    public Task<TenantDto> CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TenantDto>(HttpMethod.Post, "api/tenants", request, cancellationToken);

    public Task<TenantDto> UpdateTenantAsync(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TenantDto>(HttpMethod.Put, $"api/tenants/{tenantId}", request, cancellationToken);

    public Task DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tenants/{tenantId}", body: null, cancellationToken);

    // Agents

    public Task<PagedResult<AgentDto>> GetAgentsAsync(
        AgentStatus? status = null, string? search = null, int page = 1, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var query = $"api/agents?page={page}&pageSize={pageSize}";
        if (status is not null)
            query += $"&status={status}";
        if (!string.IsNullOrWhiteSpace(search))
            query += $"&search={Uri.EscapeDataString(search)}";
        return GetAsync<PagedResult<AgentDto>>(query, cancellationToken);
    }

    public Task<AgentDto> UpdateAgentAsync(Guid agentId, UpdateAgentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AgentDto>(HttpMethod.Put, $"api/agents/{agentId}", request, cancellationToken);

    public Task DeleteAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/agents/{agentId}", body: null, cancellationToken);

    // Commands and telemetry

    public Task<AgentCommandDto> CreateCommandAsync(Guid agentId, CreateAgentCommandRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AgentCommandDto>(HttpMethod.Post, $"api/agents/{agentId}/commands", request, cancellationToken);

    public Task<PagedResult<TelemetrySampleDto>> GetTelemetryAsync(Guid agentId, int pageSize = 20, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<TelemetrySampleDto>>($"api/agents/{agentId}/telemetry?pageSize={pageSize}", cancellationToken);

    private async Task<T> GetAsync<T>(string uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object? body, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(CreateRequest(method, uri, body), cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task SendAsync(HttpMethod method, string uri, object? body, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(CreateRequest(method, uri, body), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, object? body) => new(method, uri)
    {
        Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: Signal9Json.Options),
    };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Signal9Json.Options, cancellationToken)
            ?? throw new ApiException(response.StatusCode, "The API returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        ProblemResponse? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(Signal9Json.Options, cancellationToken);
        }
        catch (Exception)
        {
            // Not a problem+json body; fall back to the status code.
        }
        throw new ApiException(response.StatusCode, problem?.Describe() ?? $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private sealed record ProblemResponse(string? Title, string? Detail, Dictionary<string, string[]>? Errors)
    {
        public string? Describe() =>
            Errors is { Count: > 0 } ? string.Join(" ", Errors.SelectMany(e => e.Value))
            : Detail ?? Title;
    }
}

public sealed class ApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
