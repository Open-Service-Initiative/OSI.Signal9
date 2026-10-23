using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore;
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

    public const string TokenEndpoint = "connect/token";
    public const string RevocationEndpoint = "connect/revoke";
    public const string IntrospectionEndpoint = "connect/introspect";

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(10);

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
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<Signal9DbContext>());

    /// <summary>
    /// The token rules every host shares: the issuer, the allowed grants, lifetimes and keys. Password, device code
    /// and implicit are never allowed, so OpenIddict refuses them (story 83).
    /// </summary>
    public static OpenIddictServerBuilder UseSignal9TokenRules(this OpenIddictServerBuilder server, Uri issuer,
        IConfiguration configuration)
    {
        server.SetIssuer(issuer)
            .AllowClientCredentialsFlow()
            .SetAccessTokenLifetime(AccessTokenLifetime);

        // The signing and encryption keys are the ones stored in the database (ADR-0008).
        server.Services.AddSignal9Keys(configuration);

        server.UseAspNetCore().EnableTokenEndpointPassthrough();
        return server;
    }

    /// <summary>
    /// Handles token requests that OpenIddict has already authenticated and validated, issuing identity-only
    /// tokens: subject, client and scopes, never roles or tenant access (ADR-0007).
    /// </summary>
    public static IEndpointConventionBuilder MapTokenEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(TokenEndpoint, (HttpContext context) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict server request can't be retrieved.");
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
        }).AllowAnonymous();
}
