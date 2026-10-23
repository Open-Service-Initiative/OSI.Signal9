using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddSignal9Database();
builder.Services.AddSingleton(TimeProvider.System);

var identity = builder.Configuration.GetIdentityOptions();

// auth serves discovery and advertises api's RFC 8705 mTLS aliases. OpenIddict only advertises an alias for an
// endpoint the host has itself, so auth also has token, revocation and introspection endpoints, for clients that
// don't use certificates.
builder.Services.AddSignal9OpenIddict()
    .AddCore(core => core.UseQuartz())
    .AddServer(server => server
        .UseSignal9TokenRules(identity.Issuer, builder.Environment)
        .SetTokenEndpointUris(AuthorizationServer.TokenEndpoint)
        .SetRevocationEndpointUris(AuthorizationServer.RevocationEndpoint)
        .SetIntrospectionEndpointUris(AuthorizationServer.IntrospectionEndpoint)
        .SetMtlsTokenEndpointAliasUri(new Uri(identity.ApiUri, AuthorizationServer.TokenEndpoint))
        .SetMtlsRevocationEndpointAliasUri(new Uri(identity.ApiUri, AuthorizationServer.RevocationEndpoint))
        .SetMtlsIntrospectionEndpointAliasUri(new Uri(identity.ApiUri, AuthorizationServer.IntrospectionEndpoint)));

builder.Services.AddProblemDetails();
builder.Services.AddAuthorization();

// Prunes expired and revoked tokens and authorizations hourly. Only auth runs it.
builder.Services.AddQuartz();
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapTokenEndpoint();
app.MapDefaultEndpoints();

app.Run();

namespace OSI.Signal9.Auth
{
    /// <summary>Names the auth host's entry assembly, for <c>WebApplicationFactory</c>.</summary>
    public sealed class AuthHost;
}
