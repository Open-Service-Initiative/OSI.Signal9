using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation.AspNetCore;
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

builder.AddSignal9Database();
builder.AddSignal9ForwardedHeaders();
builder.AddSignal9ClientCertificates();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<DevicePresenceOptions>().BindConfiguration(DevicePresenceOptions.SectionName);
builder.Services.AddSingleton<DevicePresence>();
builder.Services.AddOptions<TenantTreeOptions>().BindConfiguration(TenantTreeOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddScoped<TenantTree>();

builder.Services.AddControllers()
    .AddJsonOptions(options => Signal9Json.Configure(options.JsonSerializerOptions));
builder.Services.AddOpenApi();
builder.Services.AddSignalR()
    .AddJsonProtocol(options => Signal9Json.Configure(options.PayloadSerializerOptions));

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var identity = builder.Configuration.GetIdentityOptions();

// api runs the authorization server's RFC 8705 mTLS aliases (token, revocation, introspection), at the same paths
// auth advertises for them. Discovery and the key set are served only by auth.
builder.Services.AddSignal9OpenIddict()
    .AddServer(server => server
        .UseSignal9TokenRules(identity, builder.Configuration)
        .SetConfigurationEndpointUris(Array.Empty<Uri>())
        .SetJsonWebKeySetEndpointUris(Array.Empty<Uri>())
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
// Every endpoint needs a valid access token unless it says otherwise.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

var app = builder.Build();

app.UseHealthProbes();
// The certificate header is judged by the proxy's own address, so before forwarded headers replace it. Both come
// before rate limiting, which partitions by the client's address.
app.UseSignal9ClientCertificates();
app.UseSignal9ForwardedHeaders();
app.UseHostHardening();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseRateLimitingAndAuthentication();
app.UseAuthorization();

app.MapTokenEndpoint().RequireRateLimiting(RateLimiting.Token);
app.MapControllers().RequireRateLimiting(RateLimiting.Api);
app.MapHub<AgentHub>(HubRoutes.Agents).RequireRateLimiting(RateLimiting.Api);
app.MapHub<DashboardHub>(HubRoutes.Dashboard).RequireRateLimiting(RateLimiting.Api);

app.Run();

namespace OSI.Signal9.API
{
    /// <summary>Names the api host's entry assembly, for <c>WebApplicationFactory</c>.</summary>
    public sealed class ApiHost;
}
