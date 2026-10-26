using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using static OSI.Signal9.IntegrationTests.TestCertificates;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// One policy for every self-signed certificate a client registers for <c>self_signed_tls_client_auth</c> (RFC 8705
/// §2.2), whether an agent enrolls with it or an API application adds it as a credential: each rule refuses with its own
/// message on both.
/// </summary>
[TestClass]
public sealed class ClientCertificatePolicyTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public ClientCertificatePolicyTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    public enum Rule
    {
        NotPem, CaIssued, CertificateAuthority, NoDigitalSignature, NoClientAuth, Rsa2048, UnsupportedCurve, Sha1Signature,
        NotYetValid, Expired, TooLongLived, BrokenSignature,
    }

    private static string Pem(Rule rule)
    {
        var now = DateTimeOffset.UtcNow;
        using var certificate = rule switch
        {
            Rule.NotPem => null,
            Rule.CaIssued => IssuedByAuthority("CN=client", "client.example", "https://client.example", "192.0.2.1", "a@client.example"),
            Rule.CertificateAuthority => AgentCertificate(certificateAuthority: true),
            Rule.NoDigitalSignature => AgentCertificate(digitalSignature: false),
            Rule.NoClientAuth => AgentCertificate(clientAuth: false),
            Rule.Rsa2048 => AgentCertificate(AgentKey.Rsa2048),
            Rule.UnsupportedCurve => AgentCertificate(AgentKey.EcdsaBrainpoolP256),
            Rule.Sha1Signature => Sha1SignedCertificate(),
            Rule.NotYetValid => AgentCertificate(notBefore: now.AddMinutes(10)),
            Rule.Expired => AgentCertificate(notBefore: now.AddDays(-30), notAfter: now.AddMinutes(-1)),
            Rule.TooLongLived => AgentCertificate(notAfter: now.AddDays(400)),
            Rule.BrokenSignature => WithBrokenSignature(AgentCertificate()),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        return certificate?.ExportCertificatePem() ?? "not a certificate";
    }

    private static string Message(Rule rule) => rule switch
    {
        Rule.NotPem => "The certificate must be one PEM-encoded X.509 certificate.",
        Rule.CaIssued => "The certificate must be self-signed.",
        Rule.CertificateAuthority => "The certificate must not be a certificate authority.",
        Rule.NoDigitalSignature => "The certificate must have the digitalSignature key usage.",
        Rule.NoClientAuth => "The certificate must have the clientAuth extended key usage.",
        Rule.Rsa2048 or Rule.UnsupportedCurve => "The certificate's key must be ECDSA on P-256, P-384 or P-521, or RSA of at least 3072 bits.",
        Rule.Sha1Signature => "The certificate must be signed with SHA-256 or stronger.",
        Rule.NotYetValid or Rule.Expired => "The certificate must be valid now.",
        Rule.TooLongLived => "The certificate must be valid for at most 398 days.",
        Rule.BrokenSignature => "The certificate's self-signature doesn't verify.",
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").TryGetProperty(field, out var messages)
            ? [.. messages.EnumerateArray().Select(m => m.GetString()!)]
            : [];
    }

    [TestMethod]
    [DataRow(Rule.NotPem)]
    [DataRow(Rule.CaIssued)]
    [DataRow(Rule.CertificateAuthority)]
    [DataRow(Rule.NoDigitalSignature)]
    [DataRow(Rule.NoClientAuth)]
    [DataRow(Rule.Rsa2048)]
    [DataRow(Rule.UnsupportedCurve)]
    [DataRow(Rule.Sha1Signature)]
    [DataRow(Rule.NotYetValid)]
    [DataRow(Rule.Expired)]
    [DataRow(Rule.TooLongLived)]
    [DataRow(Rule.BrokenSignature)]
    public async Task An_enrolling_agent_and_an_api_application_are_refused_the_same_certificate(Rule rule)
    {
        using var superadministrator = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(superadministrator, "Contoso");
        var pem = Pem(rule);

        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var anonymous = _factory.CreateAnonymousClient();
        anonymous.DefaultRequestHeaders.Add(OSI.Signal9.Contracts.Agents.AgentProtocol.VersionHeader, Signal9ApiFactory.AgentVersion);
        var enrollment = await anonymous.PostAsJsonAsync(OSI.Signal9.Contracts.Agents.AgentProtocol.Routes.Enroll,
            Signal9ApiFactory.Enrollment(token.Secret!, AgentCertificate()) with { Certificate = pem }, Signal9Json.Options, Ct);

        using var person = await _factory.CreatePersonClientAsync(_factory.Superadministrator());
        var roles = await person.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct);
        var registered = await person.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/api-applications", new CreateApiApplicationRequest
        {
            DisplayName = "Vendor",
            RoleId = roles!.Items.Single(r => r.Name == "Viewer").Id,
            TokenEndpointAuthMethod = TokenEndpointAuthMethods.SelfSignedTlsClientAuth,
            Scopes = ["tenants.read"],
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, registered.StatusCode, await registered.Content.ReadAsStringAsync(Ct));
        var application = (await registered.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
        var credential = await person.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/api-applications/{application.Id}/credentials",
            new CreateApiApplicationCredentialRequest { CertificatePem = pem }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, enrollment.StatusCode, await enrollment.Content.ReadAsStringAsync(Ct));
        CollectionAssert.AreEqual(new[] { Message(rule) }, await ErrorsAsync(enrollment, "Certificate"));
        Assert.AreEqual(HttpStatusCode.BadRequest, credential.StatusCode, await credential.Content.ReadAsStringAsync(Ct));
        CollectionAssert.AreEqual(new[] { Message(rule) }, await ErrorsAsync(credential, "CertificatePem"));
    }

    [TestMethod]
    public async Task An_api_application_registers_a_certificate_meeting_the_policy()
    {
        using var superadministrator = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(superadministrator, "Contoso");
        using var certificate = AgentCertificate(AgentKey.Rsa3072);

        using var person = await _factory.CreatePersonClientAsync(_factory.Superadministrator());
        var roles = await person.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct);
        var registered = await person.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/api-applications", new CreateApiApplicationRequest
        {
            DisplayName = "Vendor",
            RoleId = roles!.Items.Single(r => r.Name == "Viewer").Id,
            TokenEndpointAuthMethod = TokenEndpointAuthMethods.SelfSignedTlsClientAuth,
            Scopes = ["tenants.read"],
        }, Signal9Json.Options, Ct);
        var application = (await registered.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
        var credential = await person.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/api-applications/{application.Id}/credentials",
            new CreateApiApplicationCredentialRequest { CertificatePem = certificate.ExportCertificatePem() }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, credential.StatusCode, await credential.Content.ReadAsStringAsync(Ct));
    }

    public void Dispose() => _factory.Dispose();
}
