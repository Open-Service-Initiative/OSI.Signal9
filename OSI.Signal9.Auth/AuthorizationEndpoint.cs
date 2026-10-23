using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
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
    public const string Path = "connect/authorize";

    /// <summary>Where a sign-in at Entra returns to: only ever this endpoint.</summary>
    public const string ReturnPath = "/" + Path + "?";

    public static IEndpointConventionBuilder MapAuthorizationEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMethods(Path, [HttpMethods.Get, HttpMethods.Post], async (HttpContext context) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict server request can't be retrieved.");

            if (Enum.TryParse<EntraSignIn.Rejection>(request[EntraSignIn.RejectionParameter]?.ToString(), out var rejection))
                return Deny(EntraSignIn.Describe(rejection));

            var session = await context.AuthenticateAsync(EntraSignIn.CookieScheme);
            if (!session.Succeeded)
            {
                // A form-posted request comes back as a query, so the return address holds all of it.
                var parameters = context.Request.HasFormContentType ? context.Request.Form.ToList() : context.Request.Query.ToList();
                var returnUrl = QueryHelpers.AddQueryString(ReturnPath.TrimEnd('?'),
                    parameters.Where(p => p.Key != EntraSignIn.RejectionParameter));
                return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, [EntraSignIn.ChallengeScheme]);
            }

            if (EntraPerson.From(session.Principal!) is not { } person)
                return Deny(EntraSignIn.Describe(EntraSignIn.Rejection.NoIdentity));

            // Identity only (ADR-0007): the person, the client and the scopes. Signing in creates no user record.
            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, person.Subject);
            identity.SetScopes(request.GetScopes());
            identity.SetResources(AuthorizationServer.ApiAudience);
            identity.SetDestinations(_ => [Destinations.AccessToken, Destinations.IdentityToken]);

            return Results.SignIn(new ClaimsPrincipal(identity), properties: null,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        })
        .AllowAnonymous()
        // OpenIddict's own endpoint, described by discovery rather than the API description.
        .ExcludeFromDescription();

    private static IResult Deny(string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
