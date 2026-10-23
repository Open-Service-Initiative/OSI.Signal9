using Microsoft.AspNetCore.Components;
using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Client.Auth;

/// <summary>Signs in and out through web, which runs the OpenID Connect flow and holds the session.</summary>
public sealed class BffSession(HttpClient http, NavigationManager navigation, ILogger<BffSession> logger)
{
    /// <summary>A full-page trip to web's login, coming back to the page the person is on.</summary>
    public void SignIn()
    {
        var returnUrl = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        navigation.NavigateTo($"{BffRoutes.Login}?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
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
