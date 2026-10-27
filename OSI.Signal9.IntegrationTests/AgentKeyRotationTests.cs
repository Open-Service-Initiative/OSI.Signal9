using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Serialization;
using static OSI.Signal9.IntegrationTests.TestCertificates;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Agents rotate their key and certificate (#129, story 102): an agent replaces its certificate with a new key's, and
/// the old certificate, and every token bound to it, stops working.
/// </summary>
[TestClass]
public sealed class AgentKeyRotationTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public AgentKeyRotationTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _client = _factory.CreateSuperadministratorClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task An_agent_rotates_its_certificate_and_the_old_one_stops_working()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var next = AgentCertificate();

        var response = await RotateAsync(agent.Client, agent.Id, next);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var certificate = (await response.Content.ReadFromJsonAsync<AgentCertificateDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(Thumbprint(next), certificate.Thumbprint);
        Assert.AreEqual(new DateTimeOffset(next.NotAfter.ToUniversalTime()), certificate.NotAfter);
        Assert.IsNotNull(response.Headers.ETag);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate)).StatusCode);
        var oldToken = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        using var rotated = await _factory.CreateAgentClientAsync(agent.Id, next);
        var heartbeat = await rotated.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
    }

    [TestMethod]
    public async Task Putting_the_current_certificate_again_changes_nothing()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var next = AgentCertificate();
        (await RotateAsync(agent.Client, agent.Id, next)).EnsureSuccessStatusCode();
        using var rotated = await _factory.CreateAgentClientAsync(agent.Id, next);

        var again = await RotateAsync(rotated, agent.Id, next);

        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        var heartbeat = await rotated.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
        var entries = await _client.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct);
        Assert.ContainsSingle(entries!.Items.Where(e => e.Action == AuditActions.AgentCertificateRotated));
    }

    [TestMethod]
    public async Task Rotation_refuses_certificates_that_are_not_a_strong_self_signed_client_certificate()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var weak = AgentCertificate(AgentKey.Rsa2048);
        using var noClientAuth = AgentCertificate(clientAuth: false);

        var weakResponse = await RotateAsync(agent.Client, agent.Id, weak);
        var noClientAuthResponse = await RotateAsync(agent.Client, agent.Id, noClientAuth);
        var garbage = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/certificate",
            new AgentCertificateRequest { Certificate = "not a certificate" }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, weakResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, noClientAuthResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate)).StatusCode);
    }

    [TestMethod]
    public async Task Only_the_agent_itself_can_rotate_its_certificate()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var other = await _factory.EnrollAgentAsync(tenant.Id, "PC02");
        using var application = await _factory.CreateUnregisteredClientAsync(OSI.Signal9.Server.Identity.Scopes.Agent);
        using var next = AgentCertificate();

        var byOtherAgent = await RotateAsync(other.Client, agent.Id, next);
        var byApplication = await RotateAsync(application, agent.Id, next);
        var byPerson = await RotateAsync(_client, agent.Id, next);
        using var anonymous = _factory.CreateAnonymousClient();
        var byAnonymous = await RotateAsync(anonymous, agent.Id, next);

        Assert.AreEqual(HttpStatusCode.Forbidden, byOtherAgent.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, byApplication.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, byPerson.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, byAnonymous.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate)).StatusCode);
    }

    [TestMethod]
    public async Task Rotation_honours_if_match()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var next = AgentCertificate();

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"api/v1/agents/{agent.Id}/certificate")
        {
            Content = JsonContent.Create(new AgentCertificateRequest { Certificate = next.ExportCertificatePem() }, options: Signal9Json.Options),
        };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale\""));
        var refused = await agent.Client.SendAsync(stale, Ct);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate)).StatusCode);
    }

    [TestMethod]
    public async Task A_person_with_devices_read_sees_the_agents_current_certificate()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var reader = await _factory.CreateApplicationClientAsync(tenant.Id, "devices.read");
        using var stranger = await _factory.CreateApplicationClientAsync(tenant.Id, "tenants.read");

        var response = await reader.GetAsync($"api/v1/agents/{agent.Id}/certificate", Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(Thumbprint(agent.Certificate),
            (await response.Content.ReadFromJsonAsync<AgentCertificateDto>(Signal9Json.Options, Ct))!.Thumbprint);
        Assert.IsNotNull(response.Headers.ETag);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await stranger.GetAsync($"api/v1/agents/{agent.Id}/certificate", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await reader.GetAsync($"api/v1/agents/{Guid.NewGuid()}/certificate", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Rotation_is_audited_as_the_agent_naming_both_certificates()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var next = AgentCertificate();

        (await RotateAsync(agent.Client, agent.Id, next)).EnsureSuccessStatusCode();

        var entries = await _client.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct);
        var rotated = Assert.ContainsSingle(entries!.Items.Where(e => e.Action == AuditActions.AgentCertificateRotated));
        Assert.AreEqual(tenant.Id, rotated.TenantId);
        Assert.AreEqual("agent", rotated.TargetType);
        Assert.AreEqual(agent.Id, rotated.TargetId);
        Assert.AreEqual(agent.DeviceId, rotated.DeviceId);
        Assert.AreEqual(AuditActorKind.Agent, rotated.Actor.Kind);
        Assert.AreEqual($"{Thumbprint(agent.Certificate)} -> {Thumbprint(next)}", rotated.Detail);
    }

    private Task<HttpResponseMessage> RotateAsync(HttpClient client, Guid agentId, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) =>
        client.PutAsJsonAsync($"api/v1/agents/{agentId}/certificate",
            new AgentCertificateRequest { Certificate = certificate.ExportCertificatePem() }, Signal9Json.Options, Ct);

    private static string Thumbprint(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) =>
        Base64UrlEncoder.Encode(SHA256.HashData(certificate.RawData));

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
