using System.Net;
using Microsoft.AspNetCore.TestHost;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Forwarded headers are believed only from configured proxies (story 129). Container Apps' ingress terminates TLS, so
/// a host sees plain HTTP plus <c>X-Forwarded-Proto: https</c>, and OpenIddict accepts only HTTPS requests.
/// </summary>
[TestClass]
public sealed class ForwardedHeadersTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public ForwardedHeadersTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    [DataRow("auth")]
    [DataRow("api")]
    public async Task Forwarded_scheme_is_believed_only_from_a_trusted_proxy(string host)
    {
        await _factory.SeedClientAsync("reporting-app");
        var server = host == "auth" ? _factory.Auth.Server : _factory.Server;

        var fromProxy = await RequestTokenOverHttpAsync(server, Signal9ApiFactory.TrustedProxy);
        var fromElsewhere = await RequestTokenOverHttpAsync(server, Signal9ApiFactory.UntrustedPeer);

        Assert.AreEqual(HttpStatusCode.OK, fromProxy.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, fromElsewhere.StatusCode);
    }

    private async Task<HttpResponseMessage> RequestTokenOverHttpAsync(TestServer server, IPAddress peer)
    {
        using var client = Signal9ApiFactory.CreatePeerClient(server, peer, null);
        client.BaseAddress = new Uri("http://localhost");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        return await client.PostAsync("connect/token", Signal9ApiFactory.ClientCredentials("reporting-app"), Ct);
    }

    public void Dispose() => _factory.Dispose();
}
