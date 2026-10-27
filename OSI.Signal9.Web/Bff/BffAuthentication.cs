using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// How a person signs in to the dashboard (ADR-0003): web is a confidential client of auth using authorization code
/// with PKCE, redeems the code at api's mTLS token alias with its own certificate, so the tokens are bound to it
/// (RFC 8705), and keeps them in <see cref="DashboardSessions"/>. The browser only ever gets web's session cookie.
/// </summary>
public static class BffAuthentication
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string ChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>
    /// The session cookie (story 46, RFC 10017 §6.1): <c>__Host-Http-</c>, so browsers only accept it Secure, HttpOnly,
    /// with Path=/ and no Domain, from web itself.
    /// </summary>
    public const string CookieName = "__Host-Http-signal9";

    /// <summary>
    /// web's challenge on every 401 (RFC 9110 §15.5.2 requires one): its session cookie scheme, and where to sign in.
    /// The scheme and realm follow the syntax of RFC 9110 §11.3; <c>Cookie</c> is the name the cookie-based HTTP
    /// authentication draft (draft-broyer-http-cookie-auth) used. It is unregistered, so only the dashboard acts on it.
    /// </summary>
    public const string Challenge = $"Cookie realm=\"signal9\", login=\"/{BffRoutes.Login}\"";

    /// <summary>Answers 401 with <see cref="Challenge"/>: the person has no session, or it has ended.</summary>
    public static void Unauthorized(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = Challenge;
    }

    public static WebApplicationBuilder AddSignal9Bff(this WebApplicationBuilder builder)
    {
        var identity = builder.Configuration.GetIdentityOptions();

        builder.Services.AddScoped<DashboardSessions>();
        builder.Services.AddSingleton<WebTokenClient>();
        builder.Services.AddHostedService<DashboardSessionSweeper>();
        builder.Services.AddAuthentication(CookieScheme)
            .AddCookie(CookieScheme, cookies =>
            {
                cookies.Cookie.Name = CookieName;
                cookies.Cookie.Path = "/";
                cookies.Cookie.HttpOnly = true;
                cookies.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookies.Cookie.SameSite = SameSiteMode.Strict;
                // The ticket lasts as long as the session can (story 48). The hour idle is judged by the session itself,
                // to the minute, since a sliding cookie is only renewed once half its time is up.
                cookies.ExpireTimeSpan = AuthorizationServer.DashboardSessionLifetime;
                cookies.SlidingExpiration = false;
                cookies.Events = new CookieAuthenticationEvents
                {
                    // The dashboard calls web with fetch, so a missing session is a status code, never a redirect.
                    OnRedirectToLogin = context =>
                    {
                        Unauthorized(context.Response);
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    },
                    // A ticket whose session has ended (signed out on any replica, idle or capped) is worth nothing.
                    OnValidatePrincipal = async context =>
                    {
                        var sessions = context.HttpContext.RequestServices.GetRequiredService<DashboardSessions>();
                        if (!await sessions.ValidateAsync(context.Principal!, context.HttpContext.RequestAborted))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieScheme);
                        }
                    },
                };
            })
            .AddOpenIdConnect(ChallengeScheme, oidc =>
            {
                oidc.Authority = identity.Issuer.ToString();
                oidc.ClientId = WebClientRegistration.ClientId;
                oidc.SignInScheme = CookieScheme;
                oidc.CallbackPath = WebClientRegistration.CallbackPath;
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                // The code comes back in the query of a top-level GET, which needs no script at auth to post a form.
                oidc.ResponseMode = OpenIdConnectResponseMode.Query;
                oidc.UsePkce = true;
                // auth's pushed authorization endpoint can't authenticate web, whose only credential is its certificate,
                // and api has no mTLS alias for it yet.
                oidc.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                // Tokens never go into the cookie; DashboardSessions keeps them.
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = false;
                oidc.MapInboundClaims = false;
                oidc.DisableTelemetry = true;
                oidc.Scope.Clear();
                // offline_access brings the refresh token web keeps the session going with (#118).
                foreach (var scope in WebClientRegistration.Scopes)
                    oidc.Scope.Add(scope);
                oidc.TokenValidationParameters.NameClaimType = "sub";
                oidc.Events = new OpenIdConnectEvents
                {
                    OnAuthorizationCodeReceived = RedeemAtApiAsync,
                    OnTokenValidated = StartSessionAsync,
                };
            });

        // ID tokens expire on the same clock as everything else, with IdentityModel's usual rule: an expiry is required,
        // and both ends allow the clock skew.
        builder.Services.AddOptions<OpenIdConnectOptions>(ChallengeScheme)
            .PostConfigure<TimeProvider>((oidc, time) => oidc.TokenValidationParameters.LifetimeValidator =
                (notBefore, expires, _, parameters) =>
                {
                    var now = time.GetUtcNow().UtcDateTime;
                    return expires is { } expiry && now < expiry.ToUniversalTime() + parameters.ClockSkew
                        && (notBefore is not { } start || start.ToUniversalTime() <= now + parameters.ClockSkew);
                });
        return builder;
    }

    /// <summary>
    /// Redeems the code at api's RFC 8705 token alias, authenticating with web's certificate
    /// (<c>self_signed_tls_client_auth</c>): no secret, and the access token comes back bound to the certificate.
    /// </summary>
    private static async Task RedeemAtApiAsync(AuthorizationCodeReceivedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var identity = services.GetRequiredService<IConfiguration>().GetIdentityOptions();
        var request = context.TokenEndpointRequest!;

        using var client = services.GetRequiredService<ApiChannel>().CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(identity.ApiUri, AuthorizationServer.TokenEndpoint))
        {
            Content = new FormUrlEncodedContent(request.Parameters
                .Where(p => p.Value is not null && p.Key != OpenIdConnectParameterNames.ClientSecret)),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(message, context.HttpContext.RequestAborted);
        var tokens = new OpenIdConnectMessage(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));

        if (!response.IsSuccessStatusCode || !string.IsNullOrEmpty(tokens.Error))
        {
            context.Fail(new OpenIdConnectProtocolException(
                $"api's token endpoint refused web's code: {tokens.Error} {tokens.ErrorDescription}"));
            return;
        }

        context.HandleCodeRedemption(tokens);
    }

    /// <summary>
    /// Keeps the tokens in a new session, in place of any the browser had, and gives the cookie a ticket naming only the person and that session.
    /// </summary>
    private static async Task StartSessionAsync(TokenValidatedContext context)
    {
        var subject = context.Principal?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject) || context.TokenEndpointResponse?.AccessToken is not { Length: > 0 })
        {
            context.Fail("auth's response named no person or held no access token.");
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<DashboardSessions>();
        // Signing in again, such as a step-up for a recent MFA (#120), replaces the browser's session: the old one's
        // tokens are dropped and its refresh token revoked.
        // The callback runs before the cookie is read for the request, so it's read here.
        if (await context.HttpContext.AuthenticateAsync(CookieScheme) is { Succeeded: true, Principal: { } previous })
            await sessions.EndAsync(previous);
        var session = await sessions.StartAsync(subject, context.TokenEndpointResponse, context.HttpContext.RequestAborted);

        var ticket = new ClaimsIdentity(CookieScheme, "sub", "role");
        ticket.AddClaim(new Claim("sub", subject));
        ticket.AddClaim(new Claim(DashboardSessions.SessionClaim, session.ToString()));
        context.Principal = new ClaimsPrincipal(ticket);
    }
}
