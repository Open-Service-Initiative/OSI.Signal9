using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Keys;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The OpenIddict authorization server, as both <c>auth</c> and <c>api</c> run it (ADR-0002, ADR-0003): one database,
/// one set of keys and one static issuer. Each host turns on only its own endpoints; the token rules live here so they
/// can't drift apart.
/// </summary>
public static class AuthorizationServer
{
    /// <summary>The audience of every access token: the <c>api</c> host's resources.</summary>
    public const string ApiAudience = "signal9-api";

    /// <summary>The authorization endpoint, on <c>auth</c> only.</summary>
    public const string AuthorizationEndpoint = "connect/authorize";

    public const string TokenEndpoint = "connect/token";
    public const string RevocationEndpoint = "connect/revoke";
    public const string IntrospectionEndpoint = "connect/introspect";

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Authorization codes last 5 minutes and work once (RFC 6749 §4.1.2).</summary>
    public static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Reads and validates the <see cref="IdentityOptions"/> section.</summary>
    public static IdentityOptions GetIdentityOptions(this IConfiguration configuration)
    {
        var options = configuration.GetSection(IdentityOptions.SectionName).Get<IdentityOptions>() ?? new IdentityOptions();
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
        return options;
    }

    /// <summary>Adds OpenIddict's stores, on the shared <see cref="Signal9DbContext"/>.</summary>
    public static OpenIddictBuilder AddSignal9OpenIddict(this IServiceCollection services) =>
        services.AddOpenIddict()
            .AddCore(core =>
            {
                core.UseEntityFrameworkCore().UseDbContext<Signal9DbContext>();
                core.ReplaceApplicationManager(typeof(Signal9ApplicationManager<>));
            });

    /// <summary>
    /// The token rules every host shares: the issuer, the allowed grants and scopes, lifetimes and keys. Password, device code
    /// and implicit are never allowed, so OpenIddict refuses them (story 83).
    /// </summary>
    public static OpenIddictServerBuilder UseSignal9TokenRules(this OpenIddictServerBuilder server,
        IdentityOptions identity, IConfiguration configuration)
    {
        server.SetIssuer(identity.Issuer)
            .AllowClientCredentialsFlow()
            .SetAccessTokenLifetime(AccessTokenLifetime)
            // Only catalogue scopes are issued; anything else is invalid_scope.
            .RegisterScopes([.. Scopes.All.Keys])
            .UseSignal9ProofOfPossession(identity);

        // The signing and encryption keys are the ones stored in the database (ADR-0008).
        server.Services.AddSignal9Keys(configuration);

        server.UseAspNetCore().EnableTokenEndpointPassthrough();
        return server;
    }

    /// <summary>
    /// The authorization code flow, as auth issues codes and auth and api redeem them: codes last 5 minutes, and PKCE
    /// is S256 only (RFC 9700 §2.1.1); "plain" is refused.
    /// </summary>
    public static OpenIddictServerBuilder UseSignal9CodeFlow(this OpenIddictServerBuilder server) =>
        server.AllowAuthorizationCodeFlow()
            .SetAuthorizationCodeLifetime(AuthorizationCodeLifetime)
            .Configure(options => options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

    /// <summary>
    /// Handles token requests that OpenIddict has already authenticated and validated, issuing identity-only
    /// tokens: subject, client and scopes, never roles or tenant access (ADR-0007). An authorization code's principal
    /// was built at the authorization endpoint; OpenIddict has checked its PKCE verifier and that it wasn't used before.
    /// </summary>
    public static IEndpointConventionBuilder MapTokenEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(TokenEndpoint, async (HttpContext context) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict server request can't be retrieved.");

            if (request.IsAuthorizationCodeGrantType())
            {
                var code = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                return Results.SignIn(code.Principal!, properties: null,
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            if (!request.IsClientCredentialsGrantType())
                throw new InvalidOperationException($"The grant type '{request.GrantType}' isn't handled.");

            // An application acting as itself is its own subject (RFC 9068 §2.2).
            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, request.ClientId);
            identity.SetScopes(request.GetScopes());
            identity.SetResources(ApiAudience);
            identity.SetDestinations(_ => [Destinations.AccessToken]);

            return Results.SignIn(new ClaimsPrincipal(identity), properties: null,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        })
        .AllowAnonymous()
        // OpenIddict's own endpoint, described by discovery rather than the API description.
        .ExcludeFromDescription();
}
