using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Keys;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OidcScopes = OpenIddict.Abstractions.OpenIddictConstants.Scopes;

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

    /// <summary>
    /// The pushed authorization request endpoint (RFC 9126), on <c>auth</c> only: optional for every client, and not
    /// offered at an mTLS alias until OpenIddict supports one.
    /// </summary>
    public const string PushedAuthorizationEndpoint = "connect/par";

    public const string TokenEndpoint = "connect/token";
    public const string RevocationEndpoint = "connect/revoke";
    public const string IntrospectionEndpoint = "connect/introspect";

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a rotated refresh token keeps working, so a client whose response was lost can retry. Reuse after it
    /// revokes the whole chain (RFC 9700 §4.14.2).
    /// </summary>
    public static readonly TimeSpan RefreshTokenReuseLeeway = TimeSpan.FromSeconds(30);

    /// <summary>The dashboard's backend-for-frontend, <c>web</c>, as registered at the authorization server.</summary>
    public const string DashboardClientId = "signal9-web";

    /// <summary>
    /// A dashboard session's absolute cap (story 48): web's session, and its refresh tokens, which rotate but never
    /// slide past it. auth's own session has the same cap, so reaching it means a fresh sign-in at Entra (story 49).
    /// </summary>
    public static readonly TimeSpan DashboardSessionLifetime = TimeSpan.FromHours(12);

    /// <summary>A dashboard session ends after this long without use (story 48), at web and at auth.</summary>
    public static readonly TimeSpan DashboardSessionIdleTimeout = TimeSpan.FromHours(1);

    /// <summary>An API application's refresh token expires after this long unused (#63).</summary>
    public static readonly TimeSpan ApplicationRefreshTokenIdleTimeout = TimeSpan.FromDays(14);

    /// <summary>However often they rotate, an API application's refresh tokens stop this long after sign-in (#63).</summary>
    public static readonly TimeSpan ApplicationRefreshTokenCap = TimeSpan.FromDays(90);

    /// <summary>When a refresh token chain reaches <see cref="ApplicationRefreshTokenCap"/>, carried only in refresh tokens.</summary>
    private const string RefreshCapClaim = "signal9_refresh_cap";

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
            // The shared refresh policy: opaque reference tokens, so they can be revoked, rotated on every use (the
            // default) with a 30-second reuse leeway; reuse after it revokes the chain (RFC 9700 §4.14.2).
            .AllowRefreshTokenFlow()
            .UseReferenceRefreshTokens()
            .SetRefreshTokenReuseLeeway(RefreshTokenReuseLeeway)
            // Only catalogue scopes are issued; anything else is invalid_scope.
            .RegisterScopes([.. Scopes.All.Keys])
            .UseSignal9ProofOfPossession(identity);

        // The signing and encryption keys are the ones stored in the database (ADR-0008).
        server.Services.AddSignal9Keys(configuration);

        // A client that fails to authenticate counts toward the repeated-failure audit (story 121).
        server.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(handler => handler.UseInlineHandler(async context =>
        {
            if (context.Error == Errors.InvalidClient && context.Transaction.GetHttpRequest()?.HttpContext is { } http)
                await http.RequestServices.GetRequiredService<AuthenticationFailures>().RecordAsync(http, context.Request?.ClientId, http.RequestAborted);
        }));

        // An API application authenticates only with the method it registered (RFC 7591 token_endpoint_auth_method). The
        // application manager enforces it for certificates and secrets; a client assertion is refused here, because a
        // self-signed certificate's key could otherwise sign one too.
        server.AddEventHandler<OpenIddictServerEvents.ProcessAuthenticationContext>(handler => handler.UseInlineHandler(async context =>
        {
            if (string.IsNullOrEmpty(context.Request?.ClientAssertion) || context.Request.ClientId is not { } clientId
                || context.Transaction.GetHttpRequest()?.HttpContext is not { } http)
                return;
            var applications = http.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
            if (await applications.FindByClientIdAsync(clientId, http.RequestAborted) is { } application
                && ApiApplicationClient.AuthMethod(await applications.GetPropertiesAsync(application, http.RequestAborted)) is { } method
                && method != Contracts.ApiApplications.TokenEndpointAuthMethods.PrivateKeyJwt)
            {
                context.Reject(Errors.InvalidClient, "This client doesn't authenticate with private_key_jwt.");
            }
        }).SetOrder(int.MinValue + 100_000));

        server.UseAspNetCore().EnableTokenEndpointPassthrough();
        return server;
    }

    /// <summary>
    /// The authorization code flow, as auth issues codes and auth and api redeem them: codes last 5 minutes, and PKCE
    /// is S256 only (RFC 9700 §2.1.1); "plain" is refused. A client registered without PKCE must send an OpenID Connect
    /// request with a nonce instead (story 86, RFC 9700 §2.1.1), at the authorization and PAR endpoints alike.
    /// </summary>
    public static OpenIddictServerBuilder UseSignal9CodeFlow(this OpenIddictServerBuilder server) =>
        server.AllowAuthorizationCodeFlow()
            .SetAuthorizationCodeLifetime(AuthorizationCodeLifetime)
            .Configure(options => options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain))
            .AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler => handler
                .UseInlineHandler(async context =>
                {
                    if (await NonceRefusalAsync(context.Transaction, context.ClientId, context.Request) is { } refusal)
                        context.Reject(Errors.InvalidRequest, refusal);
                })
                .SetOrder(OpenIddictServerHandlers.Authentication.ValidateProofKeyForCodeExchangeRequirement.Descriptor.Order + 1))
            .AddEventHandler<OpenIddictServerEvents.ValidatePushedAuthorizationRequestContext>(handler => handler
                .UseInlineHandler(async context =>
                {
                    if (await NonceRefusalAsync(context.Transaction, context.ClientId, context.Request) is { } refusal)
                        context.Reject(Errors.InvalidRequest, refusal);
                })
                .SetOrder(OpenIddictServerHandlers.Authentication.ValidatePushedProofKeyForCodeExchangeRequirement.Descriptor.Order + 1));

    /// <summary>
    /// Why the request is refused for lacking a nonce, or null: a client that requires nonce, or doesn't require PKCE,
    /// must make an OpenID Connect request (the nonce comes back only in the ID token) with a nonce.
    /// </summary>
    private static async Task<string?> NonceRefusalAsync(OpenIddictServerTransaction transaction, string? clientId,
        OpenIddictRequest request)
    {
        if (clientId is null || transaction.GetHttpRequest()?.HttpContext is not { } http)
            return null;
        var applications = http.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(clientId, http.RequestAborted) is not { } application)
            return null;
        var required = ApiApplicationClient.RequiresNonce(await applications.GetPropertiesAsync(application, http.RequestAborted))
            || !await applications.HasRequirementAsync(application, Requirements.Features.ProofKeyForCodeExchange, http.RequestAborted);
        return required && (string.IsNullOrEmpty(request.Nonce) || !request.HasScope(OidcScopes.OpenId))
            ? "This client doesn't use PKCE, so it must send an OpenID Connect request (scope openid) with a nonce."
            : null;
    }

    /// <summary>
    /// Handles token requests that OpenIddict has already authenticated and validated, issuing identity-only
    /// tokens: subject, client and scopes, never roles or tenant access (ADR-0007). An authorization code's principal
    /// was built at the authorization endpoint; OpenIddict has checked its PKCE verifier and that it wasn't used before.
    /// </summary>
    public static IEndpointConventionBuilder MapTokenEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(TokenEndpoint, async (HttpContext context, TimeProvider time) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict server request can't be retrieved.");

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                var grant = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                var principal = grant.Principal!;
                if (request.ClientId == DashboardClientId)
                {
                    // web's refresh tokens end with the session: 12 hours from sign-in, however often they rotate.
                    principal.SetRefreshTokenLifetime(request.IsRefreshTokenGrantType()
                        ? principal.GetExpirationDate()!.Value - time.GetUtcNow()
                        : DashboardSessionLifetime);
                }
                else
                {
                    // Anyone else's last 14 idle days, and 90 days from sign-in however often they rotate. The cap rides
                    // in the refresh token only: the claim has no destination.
                    var now = time.GetUtcNow();
                    var cap = request.IsAuthorizationCodeGrantType()
                        ? now + ApplicationRefreshTokenCap
                        // A chain without a cap (from before it existed) ends now: fail closed.
                        : principal.GetClaim(RefreshCapClaim) is { } claim
                            ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(claim, CultureInfo.InvariantCulture))
                            : now;
                    principal.SetClaim(RefreshCapClaim, cap.ToUnixTimeSeconds());
                    principal.SetRefreshTokenLifetime(cap - now < ApplicationRefreshTokenIdleTimeout ? cap - now : ApplicationRefreshTokenIdleTimeout);
                }
                return Results.SignIn(principal, properties: null,
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            if (!request.IsClientCredentialsGrantType())
                throw new InvalidOperationException($"The grant type '{request.GrantType}' isn't handled.");

            // A revoked agent still holds its certificate, so it authenticates; it's told it was revoked, so it stops
            // rather than retrying or enrolling again (#129). Only an authenticated client learns this.
            if (Guid.TryParse(request.ClientId, out var agentId)
                && await context.RequestServices.GetRequiredService<Signal9DbContext>().Agents
                    .IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
                    .AnyAsync(a => a.Id == agentId && a.RevokedAt != null, context.RequestAborted))
            {
                return Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Contracts.Agents.AgentErrors.Revoked,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "This agent has been revoked.",
                }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
            }

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
