using OSI.Signal9.Auth;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// An invitation's link is its bearer reference (#121): request traces record only that one was opened.
builder.Services.RedactPathInTelemetry("/" + InvitationReference.Path);
builder.AddHostHardening();
builder.AddSignal9Database();
builder.AddSignal9ForwardedHeaders();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSignal9Audit();
builder.AddSignal9EntraSignIn();
builder.Services.AddScoped<Consent>();
builder.Services.AddScoped<FreshSignIns>();
// The hourly session check against Entra (#119, story 52).
builder.Services.AddScoped<EntraRefresh>();
builder.Services.AddScoped<EntraSessionChecker>();
builder.Services.AddHostedService<EntraSessionCheck>();
// The consent, invitation and front-channel logout pages: Razor components rendered on the server (static SSR).
builder.Services.AddRazorComponents();
// The consent and invitation pages' antiforgery cookie: host-only, HTTPS-only and never sent cross-site (RFC 6265bis).
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-Signal9.Antiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.FormFieldName = Consent.AntiforgeryField;
    // The pages send X-Frame-Options: DENY themselves, stricter than antiforgery's SAMEORIGIN.
    options.SuppressXFrameOptionsHeader = true;
});

var identity = builder.Configuration.GetIdentityOptions();
// An accepted invitation links to the dashboard, when web's address is configured (#121).
builder.Services.AddSingleton(identity);

// auth serves discovery and advertises api's RFC 8705 mTLS aliases. OpenIddict only advertises an alias for an
// endpoint the host has itself, so auth also has token, revocation and introspection endpoints, for clients that
// don't use certificates.
builder.Services.AddSignal9OpenIddict()
    .AddCore(core => core.UseQuartz())
    .AddServer(server => server
        .UseSignal9TokenRules(identity, builder.Configuration)
        // People sign in here only, so only auth has the authorization endpoint and the code flow (#109).
        .SetAuthorizationEndpointUris(AuthorizationServer.AuthorizationEndpoint)
        // Optional pushed authorization requests (RFC 9126, story 82), for clients that authenticate here.
        .SetPushedAuthorizationEndpointUris(AuthorizationServer.PushedAuthorizationEndpoint)
        .UseSignal9CodeFlow()
        .SetTokenEndpointUris(AuthorizationServer.TokenEndpoint)
        .SetRevocationEndpointUris(AuthorizationServer.RevocationEndpoint)
        .SetIntrospectionEndpointUris(AuthorizationServer.IntrospectionEndpoint)
        .SetMtlsTokenEndpointAliasUri(new Uri(identity.ApiUri, AuthorizationServer.TokenEndpoint))
        .SetMtlsRevocationEndpointAliasUri(new Uri(identity.ApiUri, AuthorizationServer.RevocationEndpoint))
        .SetMtlsIntrospectionEndpointAliasUri(new Uri(identity.ApiUri, AuthorizationServer.IntrospectionEndpoint))
        .UseAspNetCore().EnableAuthorizationEndpointPassthrough());

builder.Services.AddAuthorization();

// Prunes expired and revoked tokens and authorizations hourly. Only auth runs it.
builder.Services.AddQuartz();
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

var app = builder.Build();

app.UseHealthProbes();
// auth never asks for client certificates (ADR-0003), so it never reads a forwarded one either.
app.UseSignal9ForwardedHeaders();
app.UseHostHardening();
app.UseRateLimitingAndAuthentication();
app.UseAuthorization();

app.MapAuthorizationEndpoint().RequireRateLimiting(RateLimiting.Authorize);
app.MapTokenEndpoint().RequireRateLimiting(RateLimiting.Token);
app.MapFrontChannelLogout().RequireRateLimiting(RateLimiting.Authorize);
app.MapInvitationRedemption().RequireRateLimiting(RateLimiting.InvitationRedemption);

app.Run();

namespace OSI.Signal9.Auth
{
    /// <summary>Names the auth host's entry assembly, for <c>WebApplicationFactory</c>.</summary>
    public sealed class AuthHost;
}
