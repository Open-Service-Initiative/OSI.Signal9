using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OSI.Signal9.Contracts.Hubs;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// On Azure SignalR Service, the dashboard reaches the service through web, so the browser stays on web's origin
/// (ADR-0009, #63): web points the dashboard's negotiation at its own path and forwards that path to the service, and
/// only for the dashboard hub. The browser holds the service's short-lived token, never an API token.
/// </summary>
[TestClass]
public sealed class RealtimeServiceProxyTests : IDisposable
{
    private const string Service = "https://signal9-test.service.signalr.net/";
    private const string ServiceToken = "service-token";

    private readonly Signal9ApiFactory _factory = new(settings: new Dictionary<string, string?>
    {
        ["Realtime:Hosting"] = "AzureSignalR",
        ["Realtime:ServiceEndpoint"] = Service,
        ["ConnectionStrings:signalr"] = $"Endpoint={Service};AccessKey=MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=;Version=1.0;",
    });

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task Web_points_the_dashboards_negotiation_at_its_own_path_to_the_service()
    {
        using var browser = await SignedInAsync();
        // api, on Azure SignalR, redirects the client to the service with a token for it.
        _factory.Web.Fault = request => request.RequestUri!.AbsolutePath == $"{HubRoutes.Dashboard}/negotiate"
            ? Json(new { url = $"{Service}client/?hub=dashboardhub&asrs.op=%2Fhubs%2Fdashboard", accessToken = ServiceToken })
            : null;

        var negotiate = await browser.FetchAsync(HttpMethod.Post, "hubs/dashboard/negotiate?negotiateVersion=1",
            headers: h => h.Add("Origin", Signal9WebFactory.Origin));

        Assert.AreEqual(HttpStatusCode.OK, negotiate.StatusCode, await negotiate.Content.ReadAsStringAsync(Ct));
        var body = await negotiate.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.AreEqual($"{Signal9WebFactory.Origin}/hubs/dashboard/service/client/?hub=dashboardhub&asrs.op=%2Fhubs%2Fdashboard",
            body.GetProperty("url").GetString());
        Assert.AreEqual(ServiceToken, body.GetProperty("accessToken").GetString());
    }

    [TestMethod]
    public async Task Web_refuses_a_negotiation_that_redirects_anywhere_but_the_configured_service()
    {
        using var browser = await SignedInAsync();
        _factory.Web.Fault = request => request.RequestUri!.AbsolutePath == $"{HubRoutes.Dashboard}/negotiate"
            ? Json(new { url = "https://elsewhere.example/client/?hub=dashboardhub", accessToken = ServiceToken })
            : null;

        var negotiate = await browser.FetchAsync(HttpMethod.Post, "hubs/dashboard/negotiate?negotiateVersion=1",
            headers: h => h.Add("Origin", Signal9WebFactory.Origin));

        Assert.AreEqual(HttpStatusCode.BadGateway, negotiate.StatusCode);
        StringAssert.DoesNotMatch(await negotiate.Content.ReadAsStringAsync(Ct), new System.Text.RegularExpressions.Regex(ServiceToken));
    }

    [TestMethod]
    public async Task Web_forwards_the_dashboards_service_path_to_the_service_with_the_service_token_only()
    {
        using var browser = await SignedInAsync();

        var negotiate = await browser.FetchAsync(HttpMethod.Post,
            "hubs/dashboard/service/client/negotiate?hub=dashboardhub&negotiateVersion=1",
            headers: h =>
            {
                h.Add("Origin", Signal9WebFactory.Origin);
                h.Authorization = new("Bearer", ServiceToken);
            });

        Assert.AreEqual(HttpStatusCode.OK, negotiate.StatusCode, await negotiate.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual("from the service", await negotiate.Content.ReadAsStringAsync(Ct));
        var forwarded = _factory.Web.ServiceRequests.Single();
        Assert.AreEqual(new Uri($"{Service}client/negotiate?hub=dashboardhub&negotiateVersion=1"), forwarded.Uri);
        Assert.AreEqual($"Bearer {ServiceToken}", forwarded.Headers["Authorization"]);
        Assert.IsFalse(forwarded.Headers.ContainsKey("Cookie"), "web sent the browser's cookies to the service.");
        Assert.IsFalse(_factory.Web.ApiRequests.Any(r => r.Uri.AbsolutePath.Contains("/service/", StringComparison.Ordinal)),
            "The service path went to api.");
    }

    [TestMethod]
    [DataRow("hubs/dashboard/service/client/negotiate?hub=agenthub")]
    [DataRow("hubs/dashboard/service/client/negotiate")]
    [DataRow("hubs/dashboard/service/server/?hub=dashboardhub")]
    [DataRow("hubs/dashboard/service/api/v1/dashboardhub/groups/x")]
    public async Task Web_forwards_nothing_but_the_dashboard_hubs_client_path(string path)
    {
        using var browser = await SignedInAsync();

        var response = await browser.FetchAsync(HttpMethod.Post, path,
            headers: h => h.Add("Origin", Signal9WebFactory.Origin));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.IsEmpty(_factory.Web.ServiceRequests);
    }

    [TestMethod]
    public async Task The_service_path_needs_a_dashboard_session()
    {
        using var browser = new DashboardBrowser(_factory);

        var response = await browser.FetchAsync(HttpMethod.Post, "hubs/dashboard/service/client/negotiate?hub=dashboardhub",
            headers: h => h.Add("Origin", Signal9WebFactory.Origin));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(_factory.Web.ServiceRequests);
    }

    private async Task<DashboardBrowser> SignedInAsync()
    {
        var assigned = await _factory.AssignedPersonAsync();
        var browser = new DashboardBrowser(_factory);
        var landing = await browser.SignInAsync(assigned.User);
        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode, await landing.Content.ReadAsStringAsync(Ct));
        _factory.Web.RealtimeService = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("from the service", Encoding.UTF8, "text/plain"),
        };
        return browser;
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    public void Dispose() => _factory.Dispose();
}
