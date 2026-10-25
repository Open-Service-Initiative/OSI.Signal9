using System.Globalization;
using Microsoft.AspNetCore.Components;
using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Client.Auth;

/// <summary>Signs in and out through web, which runs the OpenID Connect flow and holds the session.</summary>
public sealed class BffSession(HttpClient http, NavigationManager navigation, ILogger<BffSession> logger)
{
    /// <summary>A full-page trip to web's login, coming back to the page the person is on.</summary>
    public void SignIn() => NavigateToLogin(null);

    /// <summary>
    /// Signs the person in again unless they authenticated within <paramref name="maxAge"/> seconds, after RFC 9470's
    /// step-up challenge (#120), coming back to the page they're on.
    /// </summary>
    public void StepUp(long maxAge) => NavigateToLogin(maxAge);

    private void NavigateToLogin(long? maxAge)
    {
        var returnUrl = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        var login = $"{BffRoutes.Login}?returnUrl={Uri.EscapeDataString(returnUrl)}";
        if (maxAge is { } seconds)
            login += string.Create(CultureInfo.InvariantCulture, $"&{BffRoutes.MaxAgeParameter}={seconds}");
        navigation.NavigateTo(login, forceLoad: true);
    }

    /// <summary>Ends web's session, then reloads the home page so nothing of the old session stays in memory.</summary>
    public async Task SignOutAsync()
    {
        try
        {
            using var response = await http.PostAsync(BffRoutes.Logout, content: null);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("web answered sign-out with {Status}", (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach web to sign out");
        }
        navigation.NavigateTo("/", forceLoad: true);
    }
}
