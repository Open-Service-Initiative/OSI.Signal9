using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Bff;

/// <summary>The dashboard's own endpoints at web: who's signed in, and signing in and out (<see cref="BffRoutes"/>).</summary>
public static class BffEndpoints
{
    public static WebApplication MapBff(this WebApplication app)
    {
        // 401 without a session (the cookie scheme never redirects).
        app.MapGet(BffRoutes.User, (HttpContext context) =>
        {
            var subject = context.User.FindFirst("sub")!.Value;
            return TypedResults.Ok(new BffUser(subject, subject));
        }).RequireAuthorization();

        // A full-page navigation from the dashboard. It only ever returns to a page on web (no open redirect).
        // With maxAge, after api's step-up challenge (RFC 9470, #120), auth signs the person in again unless they
        // authenticated within that many seconds (OpenID Connect's max_age).
        app.MapGet(BffRoutes.Login, (string? returnUrl, [FromQuery(Name = BffRoutes.MaxAgeParameter)] long? maxAge) =>
        {
            returnUrl = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
            if (!RedirectHttpResult.IsLocalUrl(returnUrl))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a page on this site.");
            if (maxAge is < 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "maxAge must be a number of seconds.");
            var properties = new OpenIdConnectChallengeProperties { RedirectUri = returnUrl };
            if (maxAge is { } seconds)
                properties.MaxAge = TimeSpan.FromSeconds(seconds);
            return Results.Challenge(properties, [BffAuthentication.ChallengeScheme]);
        }).AllowAnonymous();

        // Needs the CSRF header like any POST, so another site can't sign the person out.
        app.MapPost(BffRoutes.Logout, async (HttpContext context, DashboardSessions sessions) =>
        {
            await sessions.EndAsync(context.User);
            await context.SignOutAsync(BffAuthentication.CookieScheme);
            return TypedResults.NoContent();
        }).AllowAnonymous();

        return app;
    }
}
