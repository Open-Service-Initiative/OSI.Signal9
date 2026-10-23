using System.Net.Http.Headers;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.Web.Bff;

/// <summary>What came of asking api's token endpoint to refresh a session's tokens.</summary>
/// <param name="Tokens">The new tokens, when it worked.</param>
/// <param name="Rejected">
/// Whether the authorization server answered <c>invalid_grant</c> (RFC 6749 §5.2): the grant is gone for good, so the
/// session is over. Anything else that goes wrong is transient.
/// </param>
public sealed record TokenRefresh(OpenIdConnectMessage? Tokens, bool Rejected);

/// <summary>
/// web's calls to the authorization server for a session it holds, at api's RFC 8705 aliases with web's certificate
/// (<c>self_signed_tls_client_auth</c>): refreshing its tokens and revoking its refresh token.
/// </summary>
public sealed class WebTokenClient(ApiChannel channel, IConfiguration configuration, ILogger<WebTokenClient> logger)
{
    /// <summary>How long web waits for the authorization server before treating it as unavailable.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Uri _api = configuration.GetIdentityOptions().ApiUri;

    /// <summary>Exchanges a refresh token for new tokens (RFC 6749 §6).</summary>
    public async Task<TokenRefresh> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var (status, response) = await PostAsync(AuthorizationServer.TokenEndpoint, new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = WebClientRegistration.ClientId,
            }, cancellationToken);

            if (response?.Error == "invalid_grant")
                return new TokenRefresh(null, Rejected: true);
            if (status is >= 200 and < 300 && response is { Error: null or "", AccessToken.Length: > 0 })
                return new TokenRefresh(response, Rejected: false);

            logger.LogWarning("Refreshing a dashboard session failed with {Status} {Error}; it stays signed in.",
                status, response?.Error);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Refreshing a dashboard session failed; it stays signed in.");
        }
        return new TokenRefresh(null, Rejected: false);
    }

    /// <summary>Revokes a refresh token (RFC 7009). It's best-effort: a failure is logged.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var (status, response) = await PostAsync(AuthorizationServer.RevocationEndpoint, new()
            {
                ["token"] = refreshToken,
                ["token_type_hint"] = "refresh_token",
                ["client_id"] = WebClientRegistration.ClientId,
            }, cancellationToken);
            if (status is not (>= 200 and < 300))
                logger.LogWarning("Revoking a dashboard session's refresh token failed with {Status} {Error}.",
                    status, response?.Error);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Revoking a dashboard session's refresh token failed.");
        }
    }

    private async Task<(int Status, OpenIdConnectMessage? Response)> PostAsync(string endpoint,
        Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var client = channel.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_api, endpoint))
        {
            Content = new FormUrlEncodedContent(parameters),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(message, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        OpenIdConnectMessage? parsed = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json" && body.Length > 0)
        {
            try
            {
                parsed = new OpenIdConnectMessage(body);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                // Not an OAuth response, such as a proxy's error page.
            }
        }
        return ((int)response.StatusCode, parsed);
    }
}
