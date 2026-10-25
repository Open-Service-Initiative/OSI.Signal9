using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using OpenIddict.Validation.AspNetCore;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Devices;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.API.Tenants;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddHostHardening();

// Registered before the database, which otherwise defaults to a caller with no assigned tenants.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CallerAccess>();
builder.Services.AddScoped<IAssignedTenants, CallerAssignedTenants>();
builder.AddSignal9Database();
builder.AddSignal9ForwardedHeaders();
builder.AddSignal9ClientCertificates();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<DevicePresenceOptions>().BindConfiguration(DevicePresenceOptions.SectionName);
builder.Services.AddSingleton<DevicePresence>();
builder.Services.AddOptions<TenantTreeOptions>().BindConfiguration(TenantTreeOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddScoped<TenantTree>();
builder.Services.AddSignal9Audit();
builder.Services.AddScoped<CallerAudit>();

builder.Services.AddControllers()
    .AddJsonOptions(options => Signal9Json.Configure(options.JsonSerializerOptions));
builder.Services.AddExceptionHandler<ConcurrencyConflictHandler>();
var identity = builder.Configuration.GetIdentityOptions();
builder.Services.AddOpenApi(options => options
    // Relative, so "try it" calls whichever host served the document: api itself, or web's /docs, where calls go out
    // with the signed-in person's session (story 133).
    .AddDocumentTransformer((document, _, _) =>
    {
        document.Servers = [new OpenApiServer { Url = "/" }];
        return Task.CompletedTask;
    })
    .AddScopes(identity.Issuer)
    .AddOperationTransformer<DocumentedRequirements>()
    .AddOperationTransformer<RecentMfaResponses>()
    .AddOperationTransformer<ConditionalOperationTransformer>());
builder.Services.AddSingleton<AgentHubConnections>();
builder.Services.AddSignalR()
    .AddJsonProtocol(options => Signal9Json.Configure(options.PayloadSerializerOptions));

// api runs the authorization server's RFC 8705 mTLS aliases (token, revocation, introspection), at the same paths
// auth advertises for them. Discovery and the key set are served only by auth.
builder.Services.AddSignal9OpenIddict()
    .AddServer(server => server
        .UseSignal9TokenRules(identity, builder.Configuration)
        .SetConfigurationEndpointUris(Array.Empty<Uri>())
        .SetJsonWebKeySetEndpointUris(Array.Empty<Uri>())
        // web redeems its authorization codes here, with its certificate, so its tokens are bound to it (ADR-0003).
        // OpenIddict allows the code flow only on a server with an authorization endpoint (ID0077), so api names
        // auth's: an absolute URI on another host, which api never serves.
        .SetAuthorizationEndpointUris(new Uri(identity.Issuer, AuthorizationServer.AuthorizationEndpoint))
        .UseSignal9CodeFlow()
        .SetTokenEndpointUris(AuthorizationServer.TokenEndpoint)
        .SetRevocationEndpointUris(AuthorizationServer.RevocationEndpoint)
        .SetIntrospectionEndpointUris(AuthorizationServer.IntrospectionEndpoint))
    // Tokens are validated here, against the shared database and keys, never through introspection.
    .AddValidation(validation =>
    {
        validation.UseLocalServer();
        // After UseLocalServer, which would otherwise turn token entry validation back off.
        validation.EnableTokenEntryValidation();
        validation.EnableAuthorizationEntryValidation();
        validation.AddAudiences(AuthorizationServer.ApiAudience);
        validation.UseAspNetCore();
    });

builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
// Every endpoint requires a scope with [RequireScope] or says [AllowAnonymous]; an architecture test enforces it. The
// fallback is a backstop that still demands a valid access token.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddSingleton<IAuthorizationHandler, RequireScopeHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, RequireRecentMfaHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, RequireAncestorAdministratorHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationRefusalHandler>();

var app = builder.Build();

app.UseHealthProbes();
// The certificate header is judged by the proxy's own address, so before forwarded headers replace it. Both come
// before rate limiting, which partitions by the client's address.
app.UseSignal9ClientCertificates();
app.UseSignal9ForwardedHeaders();
app.UseHostHardening();

app.UseRateLimitingAndAuthentication();
// Around authorization, so it sees refusals as well as what endpoints did.
app.UseRequestAudit();
app.UseAuthorization();
app.UseCallerAccess();

app.MapTokenEndpoint().RequireRateLimiting(RateLimiting.Token);
// The API description is public (story 132): it shows what each call needs, never data.
app.MapOpenApi(ApiDocs.DocumentPattern).AllowAnonymous().RequireRateLimiting(RateLimiting.Api);
app.MapControllers().RequireRateLimitingUnlessNamed(RateLimiting.Api);
// The agent hub closes a connection when its access token expires; the agent reconnects with a fresh one (story 115).
app.MapHub<AgentHub>(HubRoutes.Agents, options => options.CloseOnAuthenticationExpiration = true)
    .RequireRateLimiting(RateLimiting.Api);
app.MapHub<DashboardHub>(HubRoutes.Dashboard).RequireRateLimiting(RateLimiting.Api);

app.Run();

namespace OSI.Signal9.API
{
    /// <summary>Names the api host's entry assembly, for <c>WebApplicationFactory</c>.</summary>
    public sealed class ApiHost;
}
