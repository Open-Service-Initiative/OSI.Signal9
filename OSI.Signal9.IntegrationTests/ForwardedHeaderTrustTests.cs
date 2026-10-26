using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using OSI.Signal9.Server.Hosting;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are believed only from a configured proxy network, and never
/// when none is configured (story 129): ASP.NET Core's own middleware trusts every peer when its lists are empty.
/// </summary>
[TestClass]
public sealed class ForwardedHeaderTrustTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string ProxyNetwork = "10.0.0.0/24";
    private static readonly IPAddress Proxy = IPAddress.Parse("10.0.0.4");
    private static readonly IPAddress Elsewhere = IPAddress.Parse("203.0.113.9");

    [TestMethod]
    public async Task Forwarded_headers_are_ignored_when_no_proxy_network_is_configured()
    {
        await using var app = await StartAsync();

        var seen = await SendForwardedAsync(app, Proxy);

        Assert.AreEqual($"{Proxy} http", seen);
    }

    [TestMethod]
    public async Task Forwarded_headers_from_a_peer_outside_the_proxy_network_are_ignored()
    {
        await using var app = await StartAsync(ProxyNetwork);

        var seen = await SendForwardedAsync(app, Elsewhere);

        Assert.AreEqual($"{Elsewhere} http", seen);
    }

    [TestMethod]
    public async Task Forwarded_headers_from_the_proxy_network_are_applied()
    {
        await using var app = await StartAsync(ProxyNetwork);

        var seen = await SendForwardedAsync(app, Proxy);

        Assert.AreEqual("198.51.100.7 https", seen);
    }

    private static async Task<WebApplication> StartAsync(params string[] knownNetworks)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        for (var i = 0; i < knownNetworks.Length; i++)
            builder.Configuration[$"{ForwardedHeadersSettings.SectionName}:KnownNetworks:{i}"] = knownNetworks[i];
        builder.AddSignal9ForwardedHeaders();

        var app = builder.Build();
        app.UseSignal9ForwardedHeaders();
        app.MapGet("/", (HttpContext context) => $"{context.Connection.RemoteIpAddress} {context.Request.Scheme}");
        await app.StartAsync();
        return app;
    }

    private async Task<string> SendForwardedAsync(WebApplication app, IPAddress peer)
    {
        using var client = new HttpClient(app.GetTestServer().CreateHandler(context =>
            context.Connection.RemoteIpAddress = peer))
        { BaseAddress = new Uri("http://localhost") };
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return await client.GetStringAsync("/", TestContext.CancellationToken);
    }
}
