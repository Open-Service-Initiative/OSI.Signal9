using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// mTLS client authentication and certificate-bound tokens at api's RFC 8705 aliases (ADR-0004). Certificates arrive
/// the way Container Apps' ingress forwards them, in <c>X-Forwarded-Client-Cert</c>.
/// </summary>
[TestClass]
public sealed class MutualTlsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public MutualTlsTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Self_signed_client_authenticates_with_the_certificate_container_apps_forwards(bool urlEncoded)
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var api = _factory.CreatePeerClient(Signal9ApiFactory.TrustedProxy);
        api.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientCertificateHeader,
            TestCertificates.ContainerAppsHeader(certificate, urlEncoded));

        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var token = await response.Content.ReadFromJsonAsync<AccessToken>(Ct);
        Assert.AreEqual("Bearer", token!.TokenType);
    }

    [TestMethod]
    public async Task Self_signed_client_authenticates_with_the_certificate_from_the_tls_handshake()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var api = _factory.CreatePeerClient(IPAddress.Loopback, certificate);

        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Forwarded_certificate_from_a_peer_that_is_not_a_trusted_proxy_is_ignored()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var api = _factory.CreatePeerClient(Signal9ApiFactory.UntrustedPeer);
        api.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientCertificateHeader, TestCertificates.ContainerAppsHeader(certificate));

        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Forwarded_certificate_is_ignored_when_forwarding_is_off()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var selfHosted = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ForwardedHeaders:ForwardClientCertificate", "false"));
        using var api = Signal9ApiFactory.CreatePeerClient(selfHosted.Server, Signal9ApiFactory.TrustedProxy, null);
        api.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientCertificateHeader, TestCertificates.ContainerAppsHeader(certificate));

        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Forged_header_never_replaces_the_certificate_from_the_tls_handshake()
    {
        using var certificate = TestCertificates.SelfSigned();
        using var forged = TestCertificates.SelfSigned("someone-else");
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        await _factory.SeedSelfSignedTlsClientAsync("other-app", forged);
        using var api = _factory.CreatePeerClient(Signal9ApiFactory.UntrustedPeer, certificate);
        api.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientCertificateHeader, TestCertificates.ContainerAppsHeader(forged));

        var asSelf = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);
        var asOther = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("other-app"), Ct);

        Assert.AreEqual(HttpStatusCode.OK, asSelf.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, asOther.StatusCode);
        Assert.AreEqual("invalid_client", await ErrorAsync(asOther));
    }

    [TestMethod]
    public async Task A_header_with_more_than_one_element_is_refused()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var api = _factory.CreatePeerClient(Signal9ApiFactory.TrustedProxy);
        // A client-supplied element that the proxy appended to, rather than replaced.
        var header = TestCertificates.ContainerAppsHeader(certificate);
        api.DefaultRequestHeaders.TryAddWithoutValidation(Signal9ApiFactory.ClientCertificateHeader, $"{header},{header}");

        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Access_token_is_bound_to_the_certificate_it_was_issued_over()
    {
        using var certificate = TestCertificates.SelfSigned();
        using var other = TestCertificates.SelfSigned("other");
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        var token = await RequestMtlsTokenAsync("agent-app", certificate);

        var withCertificate = await GetTenantsAsync(token, certificate);
        var withOtherCertificate = await GetTenantsAsync(token, other);
        var withoutCertificate = await GetTenantsAsync(token, certificate: null);

        Assert.AreEqual(HttpStatusCode.OK, withCertificate.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, withOtherCertificate.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, withoutCertificate.StatusCode);
    }

    [TestMethod]
    public async Task Access_token_carries_the_certificate_thumbprint_in_cnf()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        var token = await RequestMtlsTokenAsync("agent-app", certificate);

        // Only the token's audience sees all of its claims, so introspect as the api resource server.
        await _factory.SeedClientAsync("signal9-api");
        using var api = _factory.CreateAnonymousClient();
        var response = await api.PostAsync("connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "signal9-api",
            ["client_secret"] = Signal9ApiFactory.ClientSecret,
            ["token"] = token.Value,
        }), Ct);
        var claims = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        // RFC 8705 §3.1: the base64url SHA-256 of the DER certificate.
        var expected = Base64UrlEncoder.Encode(SHA256.HashData(certificate.RawData));
        Assert.AreEqual(expected, claims.GetProperty("cnf").GetProperty("x5t#S256").GetString());
    }

    [TestMethod]
    [DataRow("tls_client_auth_subject_dn", "CN=reporting.example, O=Example Ltd")]
    [DataRow("tls_client_auth_subject_dn", "cn=Reporting.Example,o=Example Ltd")]
    [DataRow("tls_client_auth_san_dns", "Reporting.Example")]
    [DataRow("tls_client_auth_san_uri", "https://reporting.example/app")]
    [DataRow("tls_client_auth_san_ip", "192.0.2.10")]
    [DataRow("tls_client_auth_san_email", "ops@Reporting.Example")]
    public async Task Ca_issued_client_authenticates_when_its_certificate_matches_the_registered_subject(
        string parameter, string value)
    {
        using var certificate = ReportingCertificate();
        await _factory.SeedTlsClientAsync("reporting-app", new Dictionary<string, string> { [parameter] = value });

        var token = await RequestMtlsTokenAsync("reporting-app", certificate);
        var withCertificate = await GetTenantsAsync(token, certificate);

        Assert.AreEqual(HttpStatusCode.OK, withCertificate.StatusCode);
    }

    [TestMethod]
    [DataRow("tls_client_auth_subject_dn", "CN=someone-else.example, O=Example Ltd")]
    [DataRow("tls_client_auth_subject_dn", "CN=reporting.example")]
    [DataRow("tls_client_auth_san_dns", "someone-else.example")]
    [DataRow("tls_client_auth_san_uri", "https://reporting.example/other")]
    [DataRow("tls_client_auth_san_ip", "192.0.2.11")]
    [DataRow("tls_client_auth_san_email", "Ops@reporting.example")]
    [DataRow("tls_client_auth_san_email", "reporting.example")]
    public async Task Ca_issued_client_is_refused_when_its_certificate_has_another_subject(string parameter, string value)
    {
        using var certificate = ReportingCertificate();
        await _factory.SeedTlsClientAsync("reporting-app", new Dictionary<string, string> { [parameter] = value });
        using var api = _factory.CreateForwardedCertificateClient(certificate);

        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("reporting-app"), Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("invalid_client", await ErrorAsync(response));
    }

    [TestMethod]
    public async Task Ca_issued_client_cannot_be_registered_without_exactly_one_subject()
    {
        // RFC 8705 §2.1.2: exactly one subject parameter. Without one, the client has no credential at all.
        await Assert.ThrowsAsync<OpenIddictExceptions.ValidationException>(() =>
            _factory.SeedTlsClientAsync("unregistered-app", new Dictionary<string, string>()));
        await Assert.ThrowsAsync<OpenIddictExceptions.ValidationException>(() =>
            _factory.SeedTlsClientAsync("ambiguous-app", new Dictionary<string, string>
            {
                ["tls_client_auth_subject_dn"] = "CN=reporting.example",
                ["tls_client_auth_san_dns"] = "reporting.example",
            }));
    }

    [TestMethod]
    public async Task Certificate_from_an_untrusted_authority_is_refused()
    {
        using var untrusted = TestCertificates.SelfSignedAuthorityIssued("CN=reporting.example");
        await _factory.SeedTlsClientAsync("reporting-app", new Dictionary<string, string>
        {
            ["tls_client_auth_subject_dn"] = "CN=reporting.example",
        });
        using var api = _factory.CreateForwardedCertificateClient(untrusted);

        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("reporting-app"), Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Client_revokes_and_introspects_with_its_certificate_at_the_aliases()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        var token = await RequestMtlsTokenAsync("agent-app", certificate);
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        FormUrlEncodedContent Form() => new(new Dictionary<string, string> { ["client_id"] = "agent-app", ["token"] = token.Value });

        var introspection = await api.PostAsync("connect/introspect", Form(), Ct);
        var revocation = await api.PostAsync("connect/revoke", Form(), Ct);
        var afterRevocation = await GetTenantsAsync(token, certificate);

        Assert.AreEqual(HttpStatusCode.OK, introspection.StatusCode);
        Assert.IsTrue((await introspection.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("active").GetBoolean());
        Assert.AreEqual(HttpStatusCode.OK, revocation.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
    }

    [TestMethod]
    public async Task Discovery_on_auth_advertises_mtls_client_authentication_and_bound_tokens()
    {
        using var auth = _factory.Auth.CreateClient();

        var discovery = await auth.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);

        Assert.IsTrue(discovery.GetProperty("tls_client_certificate_bound_access_tokens").GetBoolean());
        var methods = discovery.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray()
            .Select(method => method.GetString()).ToList();
        CollectionAssert.Contains(methods, "tls_client_auth");
        CollectionAssert.Contains(methods, "self_signed_tls_client_auth");
    }

    [TestMethod]
    public async Task Auth_ignores_a_forwarded_certificate()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("agent-app", certificate);
        using var auth = Signal9ApiFactory.CreatePeerClient(_factory.Auth.Server, Signal9ApiFactory.TrustedProxy, null);
        auth.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientCertificateHeader, TestCertificates.ContainerAppsHeader(certificate));

        var response = await auth.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials("agent-app"), Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public void Both_hosts_bind_access_tokens_and_public_clients_refresh_tokens_to_the_certificate()
    {
        // No grant issues refresh tokens yet, so this checks the rule rather than a token. Confidential clients'
        // refresh tokens are bound by client authentication instead (RFC 8705 §4, RFC 9700 §4.14.2).
        foreach (var services in new[] { _factory.Services, _factory.Auth.Services })
        {
            var server = services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

            Assert.IsTrue(server.UseClientCertificateBoundAccessTokens);
            Assert.IsTrue(server.UseClientCertificateBoundRefreshTokens);
        }
    }

    private static X509Certificate2 ReportingCertificate() => TestCertificates.IssuedByAuthority(
        "CN=reporting.example, O=Example Ltd", "reporting.example", "https://reporting.example/app", "192.0.2.10",
        "ops@reporting.example");

    private async Task<AccessToken> RequestMtlsTokenAsync(string clientId, X509Certificate2 certificate)
    {
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials(clientId), Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AccessToken>(Ct))!;
    }

    private async Task<HttpResponseMessage> GetTenantsAsync(AccessToken token, X509Certificate2? certificate)
    {
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return await api.GetAsync("api/v1/tenants", Ct);
    }

    private async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString();

    public void Dispose() => _factory.Dispose();
}
