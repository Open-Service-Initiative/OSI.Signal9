using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OSI.Signal9.Web.Client.Auth;
using OSI.Signal9.Web.Client.Services;

namespace OSI.Signal9.Web.Client;

public static class DashboardServices
{
    /// <summary>
    /// The dashboard's services. Everything goes to web on <paramref name="baseAddress"/>, the dashboard's own origin:
    /// web answers bff/ itself and proxies the API and the dashboard hub.
    /// </summary>
    /// <param name="primaryHandler">The handler under the CSRF handler; the browser's fetch in production.</param>
    /// <param name="hubHandler">Replaces SignalR's HTTP handler; only tests need it.</param>
    public static IServiceCollection AddDashboard(
        this IServiceCollection services, Uri baseAddress, Func<HttpMessageHandler> primaryHandler,
        Func<HttpMessageHandler, HttpMessageHandler>? hubHandler = null)
    {
        services.AddScoped(_ => new HttpClient(new CsrfHeaderHandler { InnerHandler = primaryHandler() }) { BaseAddress = baseAddress });
        services.AddAuthorizationCore();
        services.AddCascadingAuthenticationState();
        services.AddScoped<BffAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<BffAuthenticationStateProvider>());
        services.AddScoped<BffSession>();
        services.AddScoped<Signal9ApiClient>();
        services.AddScoped(sp => new DashboardHubConnection(
            baseAddress, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DashboardHubConnection>>(), hubHandler));
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
