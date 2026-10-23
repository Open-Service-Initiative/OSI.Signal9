using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace OSI.Signal9.API.Tests;

/// <summary>The operational hardening every host shares (#105): probes, errors, HSTS and rate limiting.</summary>
[TestClass]
public sealed class HostHardeningTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public HostHardeningTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private TestServer Host(string name) => name == "auth" ? _factory.Auth.Server : _factory.Server;

    private static Task<HttpContext> GetOnPortAsync(TestServer server, string path, int port) =>
        server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost", port);
            context.Request.Path = path;
            context.Connection.LocalPort = port;
        });

    [TestMethod]
    [DataRow("auth", "/health")]
    [DataRow("auth", "/alive")]
    [DataRow("api", "/health")]
    [DataRow("api", "/alive")]
    public async Task Probes_are_served_on_the_internal_port(string host, string path)
    {
        var context = await GetOnPortAsync(Host(host), path, Signal9ApiFactory.ProbePort);

        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [TestMethod]
    [DataRow("auth", "/health")]
    [DataRow("auth", "/alive")]
    [DataRow("api", "/health")]
    [DataRow("api", "/alive")]
    public async Task Probes_are_not_served_on_the_public_port(string host, string path)
    {
        // The public port, even when the caller claims the internal port in the Host header.
        var onPublicPort = await GetOnPortAsync(Host(host), path, 443);
        using var client = host == "auth" ? _factory.Auth.CreateClient() : _factory.CreateClient();
        client.BaseAddress = new Uri($"https://localhost:{Signal9ApiFactory.ProbePort}/");
        var spoofed = await client.GetAsync(path.TrimStart('/'), Ct);

        Assert.AreNotEqual(StatusCodes.Status200OK, onPublicPort.Response.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.OK, spoofed.StatusCode);
    }

    [TestMethod]
    [DataRow("auth", "/.well-known/openid-configuration")]
    [DataRow("api", "/api/v1/tenants")]
    public async Task The_internal_port_serves_only_probes(string host, string path)
    {
        var context = await GetOnPortAsync(Host(host), path, Signal9ApiFactory.ProbePort);

        Assert.AreEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [TestMethod]
    [DataRow("auth")]
    [DataRow("api")]
    public async Task Unhandled_errors_are_problem_details_without_exception_details(string host)
    {
        // Without its schema, the database throws as soon as the token request looks up the client.
        using var broken = new Signal9ApiFactory(migrated: false);
        using var client = host == "auth" ? broken.Auth.CreateClient() : broken.CreateAnonymousClient();

        var response = await client.PostAsync("connect/token", Signal9ApiFactory.ClientCredentials("test-client"), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        foreach (var leak in new[] { "Exception", "Npgsql", "42P01", " at " })
            Assert.DoesNotContain(leak, body);
    }

    [TestMethod]
    [DataRow("auth")]
    [DataRow("api")]
    public async Task Error_status_codes_are_problem_details(string host)
    {
        using var client = host == "auth" ? _factory.Auth.CreateClient() : _factory.CreateClient();

        var response = await client.GetAsync("no-such-endpoint", Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    [DataRow("auth", ".well-known/openid-configuration")]
    [DataRow("api", "api/v1/tenants")]
    public async Task Https_responses_carry_a_preload_ready_hsts_header(string host, string path)
    {
        using var client = host == "auth" ? _factory.Auth.CreateClient() : _factory.CreateClient();
        // HSTS is never sent for localhost, so call the host by a public name.
        client.BaseAddress = new Uri($"https://{host}.signal9.test/");

        var response = await client.GetAsync(path, Ct);

        // hstspreload.org: max-age of at least a year (two, as recommended), includeSubDomains and preload.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("max-age=63072000; includeSubDomains; preload",
            string.Join(", ", response.Headers.GetValues("Strict-Transport-Security")));
    }

    public void Dispose() => _factory.Dispose();
}
