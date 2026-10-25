using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Auth;

/// <summary>
/// The authorization endpoint (RFC 6749 §3.1), after OpenIddict has validated the request: the client, its redirect
/// URI, its scopes and the S256 code challenge. A person without a session at auth is sent to Entra first.
/// </summary>
public static class AuthorizationEndpoint
{
    public const string Path = AuthorizationServer.AuthorizationEndpoint;

    /// <summary>Where a sign-in at Entra returns to: only ever this endpoint.</summary>
    public const string ReturnPath = "/" + Path + "?";

    public static IEndpointConventionBuilder MapAuthorizationEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMethods(Path, [HttpMethods.Get, HttpMethods.Post], async (HttpContext context, DeployingDirectory deploying,
            TimeProvider time) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict server request can't be retrieved.");

            if (Enum.TryParse<EntraSignIn.Rejection>(request[EntraSignIn.RejectionParameter]?.ToString(), out var rejection))
                return Deny(EntraSignIn.Describe(rejection));

            var session = await context.AuthenticateAsync(EntraSignIn.CookieScheme);
            var fresh = session.Succeeded && await TakeFreshSignInAsync(context, session);
            // max_age (OpenID Connect Core §3.1.2.1): the client needs an authentication at most this old, such as after
            // an RFC 9470 step-up challenge. The person authenticates at Entra again; a sign-in that just came back from
            // there and still isn't recent enough is refused, rather than sent round again.
            // A session that doesn't say when the person authenticated is too old for any max_age.
            var authenticatedAt = session.Succeeded ? EntraSignIn.AuthenticatedAt(session.Principal!) : null;
            var tooOld = session.Succeeded && (authenticatedAt is null
                || (request.MaxAge is { } maxAge && time.GetUtcNow() - authenticatedAt > TimeSpan.FromSeconds(maxAge)));
            if (tooOld && fresh)
                return Deny(EntraSignIn.Describe(EntraSignIn.Rejection.NotFresh));

            // Superadministrator is re-read from Entra for every token (story 59), so a session that already issued
            // one goes back to Entra, whose own session signs the person straight in with their current app roles.
            if (!session.Succeeded || tooOld || (!fresh && deploying.IsSuperadministrator(session.Principal!)))
            {
                // A form-posted request comes back as a query, so the return address holds all of it.
                var parameters = context.Request.HasFormContentType ? context.Request.Form.ToList() : context.Request.Query.ToList();
                var returnUrl = QueryHelpers.AddQueryString(ReturnPath.TrimEnd('?'),
                    parameters.Where(p => p.Key != EntraSignIn.RejectionParameter));
                var properties = new OpenIdConnectChallengeProperties { RedirectUri = returnUrl };
                // Entra's session would sign the person straight in; prompt=login makes them authenticate again.
                if (request.MaxAge is not null)
                    properties.Prompt = PromptValues.Login;
                return Results.Challenge(properties, [EntraSignIn.ChallengeScheme]);
            }

            if (EntraPerson.From(session.Principal!) is not { } person)
                return Deny(EntraSignIn.Describe(EntraSignIn.Rejection.NoIdentity));

            // Identity only (ADR-0007): the person, the client and the scopes. Signing in creates no user record.
            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, person.Subject);
            // When and how the person authenticated at Entra, which recent-MFA checks judge (story 45, RFC 9470 §4).
            identity.SetClaim(Claims.AuthenticationTime, authenticatedAt!.Value.ToUnixTimeSeconds());
            identity.SetClaims(Claims.AuthenticationMethodReference,
                [.. session.Principal!.FindAll("amr").Select(claim => claim.Value).Distinct(StringComparer.Ordinal)]);
            // The one authorization claim a token carries (ADR-0006, ADR-0007).
            if (deploying.IsSuperadministrator(session.Principal!))
                identity.AddClaim(Claims.Role, Superadministrator.Role);
            identity.SetScopes(request.GetScopes());
            identity.SetResources(AuthorizationServer.ApiAudience);
            identity.SetDestinations(_ => [Destinations.AccessToken, Destinations.IdentityToken]);

            return Results.SignIn(new ClaimsPrincipal(identity), properties: null,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        })
        .AllowAnonymous()
        // OpenIddict's own endpoint, described by discovery rather than the API description.
        .ExcludeFromDescription();

    /// <summary>
    /// Whether the session comes straight from Entra's sign-in, which it then no longer does: the next authorization
    /// request finds it already used.
    /// </summary>
    private static async Task<bool> TakeFreshSignInAsync(HttpContext context, AuthenticateResult session)
    {
        if (!session.Properties!.Items.Remove(EntraSignIn.FreshSignInItem))
            return false;
        await context.SignInAsync(EntraSignIn.CookieScheme, session.Principal!, session.Properties);
        return true;
    }

    private static IResult Deny(string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
