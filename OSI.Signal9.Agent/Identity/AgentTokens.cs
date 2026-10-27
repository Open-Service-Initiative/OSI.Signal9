using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Agent.Identity;

/// <summary>
/// The agent's access tokens: client credentials at api's mTLS token endpoint, authenticated by the agent's certificate
/// (<c>self_signed_tls_client_auth</c>, RFC 8705 §2.2). Each token is short-lived and bound to the certificate, and there
/// are no refresh tokens, so a new one is requested whenever the cached one nears its expiry.
/// </summary>
/// <param name="http">A client for api that presents the agent's certificate.</param>
public sealed class AgentTokens(Guid agentId, HttpClient http, TimeProvider time) : IDisposable
{
    /// <summary>A token this close to its expiry is replaced, so it doesn't expire in flight.</summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    /// <summary>A current access token, requesting a new one if needed.</summary>
    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && time.GetUtcNow() < _expiresAt - RenewalMargin)
                return _token;

            var requestedAt = time.GetUtcNow();
            using var response = await http.PostAsync(AgentProtocol.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = agentId.ToString(),
                ["scope"] = AgentProtocol.Scope,
            }), cancellationToken);
            await AgentApiClient.EnsureSuccessAsync(response, cancellationToken);
            var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
            _token = token.AccessToken;
            _expiresAt = requestedAt.AddSeconds(token.ExpiresIn);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets <paramref name="token"/> if it's the cached one, after api refused it.</summary>
    public void Refused(string token) => Interlocked.CompareExchange(ref _token, null, token);

    public void Dispose() => _lock.Dispose();

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>Sends each request with the agent's current access token.</summary>
public sealed class AgentTokenHandler(AgentTokens tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        // The next request gets a new token; this one fails, and the session starts over.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            tokens.Refused(token);
        return response;
    }
}
