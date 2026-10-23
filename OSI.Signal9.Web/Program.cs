using Microsoft.AspNetCore.Components.Forms;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using OSI.Signal9.Server.Keys;
using OSI.Signal9.Web.Bff;
using OSI.Signal9.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddHostHardening();
builder.AddSignal9Database();
builder.AddSignal9ForwardedHeaders();
builder.Services.AddSingleton(TimeProvider.System);

// web's client certificate and the Data Protection key ring, stored wrapped by the key-encryption key (ADR-0008).
builder.Services.AddSignal9StoredKeys(builder.Configuration, StoredKeyUse.WebClient);
// OpenIddict's stores only, for web's own registration; web runs no authorization server.
builder.Services.AddSignal9OpenIddict();
builder.Services.AddSingleton<WebClientRegistration>();
builder.Services.AddSingleton<WebClientCertificate>();
builder.Services.AddHostedService<WebClientCertificateStartup>();
builder.Services.AddSingleton<IApiHandlerFactory, SocketsApiHandlerFactory>();
builder.Services.AddSingleton<ApiChannel>();

builder.AddSignal9Bff();
builder.Services.AddAuthorization();
builder.Services.AddHttpForwarder();
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
builder.Services.AddScoped<AntiforgeryStateProvider, NoAntiforgeryState>();

var app = builder.Build();

app.UseHealthProbes();
app.UseSignal9ForwardedHeaders();
app.UseHostHardening();
app.UseContentSecurityPolicy();
app.UseCsrfHeader();
app.UseRateLimitingAndAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapBff();
app.MapApiProxy();
app.MapApiReference();
// Every page is the same WebAssembly shell, which shows a sign-in prompt when there's no session; the data stays behind
// the proxy's own checks. The shell takes no form posts, so it needs no antiforgery token: CSRF is the custom header.
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(OSI.Signal9.Web.Client.Routes).Assembly)
    .AllowAnonymous()
    .DisableAntiforgery();

app.Run();

namespace OSI.Signal9.Web
{
    /// <summary>Names the web host's entry assembly, for <c>WebApplicationFactory</c>.</summary>
    public sealed class WebHost;
}
