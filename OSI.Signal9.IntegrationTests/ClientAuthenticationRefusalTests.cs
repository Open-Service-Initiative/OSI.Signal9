using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Each way api's mTLS token endpoint refuses a client certificate (RFC 8705 §2), by the reason it gives: the client
/// gets only <c>invalid_client</c> or <c>invalid_request</c> (RFC 6749 §5.2), and the operator reads why in api's log.
/// </summary>
[TestClass]
public sealed class ClientAuthenticationRefusalTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new(settings: new Dictionary<string, string?>
    {
        // Where OpenIddict's application manager says why it refused a certificate.
        ["Logging:LogLevel:OpenIddict.Core.OpenIddictApplicationManager"] = "Information",
    });

    private readonly TestContext _testContext;

    public ClientAuthenticationRefusalTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private async Task<HttpResponseMessage> RequestAsync(string clientId, X509Certificate2 certificate)
    {
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        return await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials(clientId), Ct);
    }

    private async Task<HttpResponseMessage> RequestWithHeaderAsync(string clientId, string header)
    {
        using var api = _factory.CreatePeerClient(Signal9ApiFactory.TrustedProxy);
        api.DefaultRequestHeaders.TryAddWithoutValidation(Signal9ApiFactory.ClientCertificateHeader, header);
        return await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials(clientId), Ct);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private void AssertLogged(string reason) =>
        Assert.IsTrue(_factory.Logs.Any(entry => entry.Contains(reason, StringComparison.Ordinal)),
            $"Expected api to log \"{reason}\". It logged: {string.Join(" | ", _factory.Logs)}");

    [TestMethod]
    public async Task A_self_signed_client_presenting_a_ca_issued_certificate_is_refused()
    {
        // Registered with it directly, as no registration path would allow. A certificate that isn't self-issued is judged
        // as tls_client_auth, which the client doesn't use.
        using var issued = TestCertificates.IssuedByAuthority("CN=app", "app.example", "https://app.example", "192.0.2.1", "a@app.example");
        await _factory.SeedSelfSignedTlsClientAsync("self-signed-app", issued);

        var response = await RequestAsync("self-signed-app", issued);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("invalid_client", await ErrorAsync(response));
        AssertLogged("Client self-signed-app has no single registered tls_client_auth subject");
    }

    [TestMethod]
    public async Task A_self_signed_client_presenting_a_certificate_without_client_authentication_is_refused()
    {
        using var serverOnly = TestCertificates.AgentCertificate(clientAuth: false);
        await _factory.SeedSelfSignedTlsClientAsync("self-signed-app", serverOnly);

        var response = await RequestAsync("self-signed-app", serverOnly);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("invalid_client", await ErrorAsync(response));
        AssertLogged("isn't a self-issued client certificate");
    }

    [TestMethod]
    public async Task A_registered_self_signed_certificate_stops_authenticating_when_it_expires()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("self-signed-app", certificate);
        Assert.AreEqual(HttpStatusCode.OK, (await RequestAsync("self-signed-app", certificate)).StatusCode);

        _factory.Time.Advance(certificate.NotAfter.ToUniversalTime() - _factory.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        var response = await RequestAsync("self-signed-app", certificate);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("invalid_client", await ErrorAsync(response));
        AssertLogged("The self-signed certificate of client self-signed-app isn't valid");
        AssertLogged("NotTimeValid");
    }

    [TestMethod]
    public async Task A_tls_client_auth_client_presenting_a_self_signed_certificate_is_refused()
    {
        // A self-issued certificate is judged as self_signed_tls_client_auth, and the client registered none.
        using var certificate = TestCertificates.SelfSigned("ca-client");
        await _factory.SeedTlsClientAsync("tls-app", new Dictionary<string, string> { ["tls_client_auth_subject_dn"] = "CN=ca-client" });

        var response = await RequestAsync("tls-app", certificate);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("invalid_client", await ErrorAsync(response));
        AssertLogged("Client tls-app presented a certificate it didn't register");
    }

    [TestMethod]
    public async Task A_tls_client_auth_client_presenting_a_certificate_from_an_untrusted_authority_is_refused()
    {
        using var certificate = TestCertificates.SelfSignedAuthorityIssued("CN=ca-client");
        await _factory.SeedTlsClientAsync("tls-app", new Dictionary<string, string> { ["tls_client_auth_subject_dn"] = "CN=ca-client" });

        var response = await RequestAsync("tls-app", certificate);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("invalid_client", await ErrorAsync(response));
        AssertLogged("The certificate of client tls-app doesn't chain to a trusted authority");
    }

    [TestMethod]
    [DataRow("Hash=abc;Subject=\"CN=app\";", "X-Forwarded-Client-Cert has no Cert.")]
    [DataRow("Hash=abc;Cert=\"{pem}\";Cert=\"{pem}\";", "X-Forwarded-Client-Cert contains more than one Cert.")]
    [DataRow("Hash=abc;Cert=\"{pem}\";Subject=\"CN=app;", "X-Forwarded-Client-Cert has an unterminated quoted value.")]
    [DataRow("Hash=abc;Cert=\"{pem}\",Hash=def;Cert=\"{pem}\"", "X-Forwarded-Client-Cert must contain exactly one element.")]
    public async Task A_malformed_forwarded_certificate_header_is_refused(string header, string reason)
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("self-signed-app", certificate);

        var response = await RequestWithHeaderAsync("self-signed-app",
            header.Replace("{pem}", Uri.EscapeDataString(certificate.ExportCertificatePem()), StringComparison.Ordinal));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalid_request", await ErrorAsync(response));
        AssertLogged(reason);
    }

    [TestMethod]
    public async Task A_forwarded_header_with_escaped_quotes_and_separators_in_other_values_is_read()
    {
        using var certificate = TestCertificates.SelfSigned();
        await _factory.SeedSelfSignedTlsClientAsync("self-signed-app", certificate);
        var pem = Uri.EscapeDataString(certificate.ExportCertificatePem());

        // Envoy quotes values holding ',' or ';', and escapes quotes inside them.
        var response = await RequestWithHeaderAsync("self-signed-app",
            $"Hash=abc;Subject=\"CN=\\\"app; inc\\\",O=Contoso\";Cert=\"{pem}\";");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    public void Dispose() => _factory.Dispose();
}
