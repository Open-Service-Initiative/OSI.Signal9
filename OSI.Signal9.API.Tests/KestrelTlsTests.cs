using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// The self-hosted path: Kestrel terminates TLS and reads the client certificate from the handshake. api asks for a
/// certificate; auth never does (ADR-0003).
/// </summary>
[TestClass]
public sealed class KestrelTlsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly X509Certificate2 _serverCertificate = TestCertificates.Server();
    private readonly TestContext _testContext;

    public KestrelTlsTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Api_authenticates_the_client_by_its_tls_certificate_and_binds_the_token_to_it()
    {
        using var certificate = TestCertificates.SelfSigned();
        var api = StartOnKestrel(_factory);
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var withCertificate = CreateTlsClient(api, certificate);
        using var withoutCertificate = CreateTlsClient(api, certificate: null);

        var response = await withCertificate.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var token = (await response.Content.ReadFromJsonAsync<AccessToken>(Ct))!;
        withCertificate.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        withoutCertificate.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        Assert.AreEqual(HttpStatusCode.OK, (await withCertificate.GetAsync("api/tenants", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await withoutCertificate.GetAsync("api/tenants", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Auth_never_asks_for_a_client_certificate()
    {
        using var certificate = TestCertificates.SelfSigned();
        var auth = StartOnKestrel(_factory.Auth);
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var client = CreateTlsClient(auth, certificate);

        // The client is ready to present its certificate, but auth never requests it in the handshake, so it has
        // nothing to authenticate the client with.
        var response = await client.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Runs the host on Kestrel over HTTPS on a free loopback port, and returns its address.</summary>
    private Uri StartOnKestrel<T>(WebApplicationFactory<T> factory) where T : class
    {
        factory.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(_serverCertificate)));
        factory.StartServer();
        var addresses = factory.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>();
        return new Uri(addresses.Addresses.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/");
    }

    private HttpClient CreateTlsClient(Uri baseAddress, X509Certificate2? certificate)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // Trust exactly the test server's certificate.
                RemoteCertificateValidationCallback = (_, server, _, _) =>
                    server is not null && server.GetCertHashString() == _serverCertificate.GetCertHashString(),
            },
        };
        if (certificate is not null)
            handler.SslOptions.ClientCertificates = [TestCertificates.WithUsableKey(certificate)];
        return new HttpClient(handler) { BaseAddress = baseAddress };
    }

    public void Dispose()
    {
        _factory.Dispose();
        _serverCertificate.Dispose();
    }
}
