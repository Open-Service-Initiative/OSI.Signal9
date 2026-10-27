using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.Web.Client.Auth;

/// <summary>
/// Who is signed in, as web's session says (ADR-0003). The dashboard holds no token: it asks web for the person behind
/// its session cookie, and treats anything but a clear answer as signed out.
/// </summary>
public sealed class BffAuthenticationStateProvider(HttpClient http, ILogger<BffAuthenticationStateProvider> logger)
    : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private Task<AuthenticationState>? _state;

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state ??= LoadAsync();

    /// <summary>
    /// Asks web again, for when the session may have ended (an API call answered 401). Notifies only when the answer
    /// changed: a page that reloads on every notification and gets 401 again must not loop.
    /// </summary>
    /// <returns>Whether someone is still signed in.</returns>
    public async Task<bool> RefreshAsync()
    {
        var before = _state is null ? null : await _state;
        var after = await LoadAsync();
        if (before is null || Subject(before) != Subject(after))
        {
            _state = Task.FromResult(after);
            NotifyAuthenticationStateChanged(_state);
        }
        return Subject(after) is not null;
    }

    private static string? Subject(AuthenticationState state) => state.User.FindFirst("sub")?.Value;

    private async Task<AuthenticationState> LoadAsync()
    {
        try
        {
            using var response = await http.GetAsync(BffRoutes.User);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Anonymous;
            response.EnsureSuccessStatusCode();

            var user = await response.Content.ReadFromJsonAsync<BffUser>(Signal9Json.Options)
                ?? throw new InvalidOperationException("web answered bff/user with no body.");
            var identity = new ClaimsIdentity(
                [new Claim("sub", user.Subject), new Claim("name", user.Name)],
                authenticationType: "bff", nameType: "name", roleType: "role");
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not ask web who is signed in; treating the visitor as signed out");
            return Anonymous;
        }
    }
}
