using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Web.Client;

namespace OSI.Signal9.Web.Tests;

/// <summary>
/// A bUnit context wired like the dashboard's Program.cs, with HTTP (including the hub's negotiate) going to
/// <see cref="Http"/> and time frozen in <see cref="Time"/>.
/// </summary>
public abstract class DashboardTestContext : BunitContext
{
    internal FakeHttp Http { get; } = new();

    internal FakeTimeProvider Time { get; } = new();

    protected DashboardTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFluentUIComponents();
        Services.AddDashboard(new Uri("http://localhost/"), () => Http, hubHandler: _ => Http);
        Services.AddSingleton<TimeProvider>(Time);
        // bUnit stands in a placeholder that throws; the dashboard's real authorization is what's under test.
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        // Last: it builds the service provider. Web renders the dashboard interactive on WebAssembly.
        SetRendererInfo(new RendererInfo("WebAssembly", isInteractive: true));
    }

    protected NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    /// <summary>The most recent navigation the app asked for.</summary>
    protected NavigationHistory LastNavigation => ((BunitNavigationManager)Navigation).History.First();

    /// <summary>The app, as web hosts it, at <paramref name="path"/>.</summary>
    protected IRenderedComponent<Routes> RenderApp(string path = "/")
    {
        Navigation.NavigateTo(path);
        return Render<Routes>();
    }
}
