using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Server.Data;
using static OSI.Signal9.IntegrationTests.TestCertificates;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Agent enrollment and authentication (#128, ADR-0005, stories 94-100): an agent enrolls with an enrollment token and
/// the self-signed certificate of its new non-exportable key, then authenticates with that certificate at api's mTLS
/// alias and gets certificate-bound access tokens with the <c>agent</c> scope. Its identity comes only from its token.
/// </summary>
[TestClass]
public sealed class AgentEnrollmentTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public AgentEnrollmentTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task An_enrollment_that_matches_no_device_creates_a_device_in_the_tokens_tenant()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var certificate = AgentCertificate();

        var response = await _factory.EnrollAsync(token.Secret!, certificate, "PC01");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var agent = (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual($"/api/v1/agents/{agent.Id}", response.Headers.Location?.AbsolutePath);
        Assert.AreEqual($"/api/v1/agents/{agent.Id}", agent.Url);
        Assert.AreEqual(tenant.Id, agent.TenantId);
        Assert.AreEqual(token.Id, agent.EnrollmentTokenId);
        Assert.IsTrue(agent.IsActive);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual("PC01", device!.MachineName);
        Assert.AreEqual(agent.Id, device.ActiveAgentId);
    }

    [TestMethod]
    [DataRow(OsFamily.Windows)]
    [DataRow(OsFamily.Linux)]
    [DataRow(OsFamily.MacOS)]
    public async Task The_device_records_the_agent_s_operating_system_family(OsFamily family)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);

        var response = await _factory.EnrollAsync(token.Secret!, AgentCertificate(), hardware: r => r with { OsFamily = family });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var agent = (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(family, device!.OsFamily);
    }

    [TestMethod]
    public async Task Enrollment_needs_the_operating_system_family()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);

        var response = await _factory.EnrollAsync(token.Secret!, AgentCertificate(), hardware: r => r with { OsFamily = null });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ErrorsAsync(response);
        Assert.IsTrue(errors.ContainsKey(nameof(EnrollAgentRequest.OsFamily)), string.Join(", ", errors.Keys));
    }

    [TestMethod]
    public async Task Enrollments_never_match_devices_by_machine_name()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        using var first = await _factory.EnrollAgentAsync(tenant.Id, "PC01");
        using var second = await _factory.EnrollAgentAsync(tenant.Id, "PC01");

        Assert.AreNotEqual(first.DeviceId, second.DeviceId);
        Assert.IsTrue((await GetAgentAsync(first.Id)).IsActive);
    }

    [TestMethod]
    [DataRow(AgentKey.EcdsaP256)]
    [DataRow(AgentKey.EcdsaP384)]
    [DataRow(AgentKey.Rsa3072)]
    public async Task An_agent_enrolls_with_an_acceptable_key_and_authenticates_with_its_certificate(AgentKey key)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var certificate = AgentCertificate(key);
        var enrolled = await _factory.EnrollAsync(token.Secret!, certificate);
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode, await enrolled.Content.ReadAsStringAsync(Ct));
        var agent = (await enrolled.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;

        var response = await _factory.RequestAgentTokenAsync(agent.Id, certificate);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Enrollment_refuses_certificates_that_are_not_a_strong_self_signed_client_certificate()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        var now = DateTimeOffset.UtcNow;
        var refused = new Dictionary<string, X509Certificate2>
        {
            ["RSA 2048"] = AgentCertificate(AgentKey.Rsa2048),
            ["no clientAuth EKU"] = AgentCertificate(clientAuth: false),
            ["no digitalSignature"] = AgentCertificate(digitalSignature: false),
            ["a CA"] = AgentCertificate(certificateAuthority: true),
            ["expired"] = AgentCertificate(notBefore: now.AddDays(-30), notAfter: now.AddMinutes(-1)),
            ["not yet valid"] = AgentCertificate(notBefore: now.AddMinutes(10)),
            ["too long-lived"] = AgentCertificate(notAfter: now.AddDays(400)),
            ["CA-issued"] = IssuedByAuthority("CN=agent", "agent.example", "https://agent.example", "192.0.2.1", "a@agent.example"),
        };

        foreach (var (reason, certificate) in refused)
        {
            using (certificate)
            {
                var response = await _factory.EnrollAsync(token.Secret!, certificate);

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, reason);
                Assert.IsTrue((await ErrorsAsync(response)).ContainsKey("Certificate"), reason);
            }
        }

        var garbage = Signal9ApiFactory.Enrollment(token.Secret!, AgentCertificate()) with { Certificate = "not a certificate" };
        using var anonymous = _factory.CreateAnonymousClient();
        anonymous.DefaultRequestHeaders.Add(AgentProtocol.VersionHeader, Signal9ApiFactory.AgentVersion);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("api/v1/agents", garbage, Signal9Json.Options, Ct)).StatusCode);
        Assert.AreEqual(0, (await GetTokenAsync(token)).UseCount);
    }

    [TestMethod]
    public async Task Enrollment_needs_a_valid_enrollment_token()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var revoked = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        (await _client.PostAsync($"{revoked.Url.TrimStart('/')}:revoke", null, Ct)).EnsureSuccessStatusCode();
        var expiring = await _factory.IssueEnrollmentTokenAsync(tenant.Id, expiresAt: _factory.Time.GetUtcNow().AddHours(1));
        _factory.Time.Advance(TimeSpan.FromHours(1));

        foreach (var secret in new[] { "s9e_unknown", revoked.Secret!, expiring.Secret! })
        {
            using var certificate = AgentCertificate();
            var response = await _factory.EnrollAsync(secret, certificate);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.IsTrue((await ErrorsAsync(response)).ContainsKey("EnrollmentToken"));
        }
    }

    [TestMethod]
    public async Task A_token_with_a_use_limit_stops_working_once_used_up()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id, maxUses: 2);
        using var first = AgentCertificate();
        using var second = AgentCertificate();
        using var third = AgentCertificate();

        Assert.AreEqual(HttpStatusCode.Created, (await _factory.EnrollAsync(token.Secret!, first)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await _factory.EnrollAsync(token.Secret!, second)).StatusCode);
        var refused = await _factory.EnrollAsync(token.Secret!, third);

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        var used = await GetTokenAsync(token);
        Assert.AreEqual(2, used.UseCount);
        Assert.AreEqual(EnrollmentTokenStatus.UsedUp, used.Status);
    }

    [TestMethod]
    public async Task Concurrent_enrollments_never_exceed_a_single_use_token()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id, maxUses: 1);
        var certificates = Enumerable.Range(0, 5).Select(_ => AgentCertificate()).ToList();

        var responses = await Task.WhenAll(certificates.Select(c => _factory.EnrollAsync(token.Secret!, c)));

        Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.AreEqual(1, (await GetTokenAsync(token)).UseCount);
        certificates.ForEach(c => c.Dispose());
    }

    [TestMethod]
    public async Task Each_enrollment_counts_as_a_use_and_records_when_the_token_was_last_used()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        Assert.AreEqual(0, token.UseCount);
        Assert.IsNull(token.LastUsedAt);

        using var first = AgentCertificate();
        await _factory.EnrollAsync(token.Secret!, first);
        _factory.Time.Advance(TimeSpan.FromMinutes(3));
        using var second = AgentCertificate();
        await _factory.EnrollAsync(token.Secret!, second);

        var used = await GetTokenAsync(token);
        Assert.AreEqual(2, used.UseCount);
        Assert.AreEqual(_factory.Time.GetUtcNow(), used.LastUsedAt);
        Assert.AreEqual(EnrollmentTokenStatus.Active, used.Status);
    }

    [TestMethod]
    public async Task The_tenant_has_a_recently_enrolled_feed_newest_first_including_its_descendants()
    {
        var msp = await _factory.CreateTenantAsync(_client, "Msp");
        var client = await _factory.CreateTenantAsync(_client, "Client", parentTenantId: msp.Id);
        using var older = await _factory.EnrollAgentAsync(msp.Id, "MSP-PC");
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        using var newer = await _factory.EnrollAgentAsync(client.Id, "CLIENT-PC");

        var all = await _client.GetFromJsonAsync<PagedResult<AgentDto>>($"api/v1/tenants/{msp.Id}/agents", Signal9Json.Options, Ct);
        var own = await _client.GetFromJsonAsync<PagedResult<AgentDto>>(
            $"api/v1/tenants/{msp.Id}/agents?includeDescendants=false", Signal9Json.Options, Ct);

        CollectionAssert.AreEqual(new[] { newer.Id, older.Id }, all!.Items.Select(a => a.Id).ToArray());
        Assert.AreEqual($"/api/v1/agents/{newer.Id}", all.Items[0].Url);
        CollectionAssert.AreEqual(new[] { older.Id }, own!.Items.Select(a => a.Id).ToArray());
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"api/v1/tenants/{Guid.NewGuid()}/agents", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task The_recently_enrolled_feed_needs_devices_read()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var withoutScope = await _factory.CreateClientAsync("tenants.read");

        var response = await withoutScope.GetAsync($"api/v1/tenants/{tenant.Id}/agents", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Enrollment_is_audited_at_the_tokens_tenant()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        using var agent = await _factory.EnrollAgentAsync(tenant.Id);

        var entries = await _client.GetFromJsonAsync<PagedResult<AuditEntryDto>>(
            "api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct);
        var enrolled = Assert.ContainsSingle(entries!.Items.Where(e => e.Action == AuditActions.AgentEnrolled));
        Assert.AreEqual(tenant.Id, enrolled.TenantId);
        Assert.AreEqual("agent", enrolled.TargetType);
        Assert.AreEqual(agent.Id, enrolled.TargetId);
        Assert.AreEqual(agent.DeviceId, enrolled.DeviceId);
        Assert.AreEqual(AuditOutcome.Succeeded, enrolled.Outcome);
        Assert.AreEqual($"enrollment-token {agent.Agent.EnrollmentTokenId}", enrolled.Detail);
    }

    [TestMethod]
    public async Task Agent_tokens_carry_only_the_agent_scope_are_bound_to_the_certificate_and_come_without_refresh_tokens()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);

        var response = await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var withOfflineAccess = await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate, "agent offline_access");
        var otherScope = await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate, "devices.read");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(body.TryGetProperty("refresh_token", out _));
        Assert.AreNotEqual(HttpStatusCode.OK, withOfflineAccess.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.OK, otherScope.StatusCode);
        var claims = await IntrospectAsync(body.GetProperty("access_token").GetString()!);
        Assert.AreEqual(Base64UrlEncoder.Encode(SHA256.HashData(agent.Certificate.RawData)),
            claims.GetProperty("cnf").GetProperty("x5t#S256").GetString());
        Assert.AreEqual(agent.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.AreEqual("agent", claims.GetProperty("scope").GetString());
    }

    [TestMethod]
    public async Task An_agent_authenticates_only_with_its_own_certificate()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var other = await _factory.EnrollAgentAsync(tenant.Id, "PC02");
        using var stranger = AgentCertificate();

        var withOthers = await _factory.RequestAgentTokenAsync(agent.Id, other.Certificate);
        var withStrangers = await _factory.RequestAgentTokenAsync(agent.Id, stranger);
        using var noCertificate = _factory.CreateForwardedCertificateClient(null);
        var withNone = await noCertificate.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials(agent.Id.ToString(), "agent"), Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, withOthers.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, withStrangers.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.OK, withNone.StatusCode);
    }

    [TestMethod]
    public async Task An_agents_token_is_refused_with_another_certificate()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var thief = _factory.CreateForwardedCertificateClient(AgentCertificate());
        thief.DefaultRequestHeaders.Authorization = agent.Client.DefaultRequestHeaders.Authorization;

        var response = await thief.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task An_agent_heartbeats_fetches_work_sends_telemetry_and_reports_results_as_itself()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        var command = await QueueCommandAsync(agent.DeviceId);

        var heartbeat = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        var pending = await agent.Client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{agent.Id}/pending-commands", Signal9Json.Options, Ct);
        var telemetry = await agent.Client.PostAsJsonAsync($"api/v1/agents/{agent.Id}/telemetry", Sample(), Signal9Json.Options, Ct);
        var result = await agent.Client.PutAsJsonAsync($"{command.Url.TrimStart('/')}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(pending!.Items).Id);
        Assert.AreEqual(HttpStatusCode.Created, telemetry.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
    }

    [TestMethod]
    public async Task Claiming_another_agents_id_is_refused()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var victim = await _factory.EnrollAgentAsync(tenant.Id, "PC02");
        var command = await QueueCommandAsync(victim.DeviceId);

        var heartbeat = await agent.Client.PutAsJsonAsync($"api/v1/agents/{victim.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        var pending = await agent.Client.GetAsync($"api/v1/agents/{victim.Id}/pending-commands", Ct);
        var telemetry = await agent.Client.PostAsJsonAsync($"api/v1/agents/{victim.Id}/telemetry", Sample(), Signal9Json.Options, Ct);
        var unknown = await agent.Client.PutAsJsonAsync($"api/v1/agents/{Guid.NewGuid()}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        var result = await agent.Client.PutAsJsonAsync($"{command.Url.TrimStart('/')}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, heartbeat.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, pending.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, telemetry.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, result.StatusCode);
        Assert.AreEqual(CommandStatus.Pending,
            (await _client.GetFromJsonAsync<CommandDto>(command.Url.TrimStart('/'), Signal9Json.Options, Ct))!.Status);
    }

    [TestMethod]
    public async Task A_token_with_the_agent_scope_that_does_not_belong_to_an_agent_acts_as_no_agent()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        var command = await QueueCommandAsync(agent.DeviceId);
        using var application = await _factory.CreateClientAsync(OSI.Signal9.Server.Identity.Scopes.Agent);

        var heartbeat = await application.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        var result = await application.PutAsJsonAsync($"{command.Url.TrimStart('/')}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, heartbeat.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, result.StatusCode);
    }

    [TestMethod]
    public async Task Rotating_or_revoking_the_enrollment_token_leaves_enrolled_agents_working()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var certificate = AgentCertificate();
        var agent = (await (await _factory.EnrollAsync(token.Secret!, certificate)).Content
            .ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;

        (await _client.PostAsync($"{token.Url.TrimStart('/')}:rotate", null, Ct)).EnsureSuccessStatusCode();
        using var afterRotation = await _factory.CreateAgentClientAsync(agent.Id, certificate);
        var rotated = await afterRotation.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        (await _client.PostAsync($"{token.Url.TrimStart('/')}:revoke", null, Ct)).EnsureSuccessStatusCode();
        using var afterRevocation = await _factory.CreateAgentClientAsync(agent.Id, certificate);
        var revoked = await afterRevocation.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, rotated.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode);
    }

    [TestMethod]
    public async Task A_secret_rotated_away_while_enrolling_is_refused()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var certificate = AgentCertificate();
        // The rotation commits after enrollment found the token by its secret, just before it takes a use.
        using var racing = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<Signal9DbContext>(options => options.AddInterceptors(new ConcurrentWriter(
                _factory.ConnectionString,
                $"""UPDATE "EnrollmentTokens" SET "SecretHash" = decode(md5(random()::text), 'hex') WHERE "Id" = '{token.Id}'""")))));
        using var anonymous = racing.Server.CreateClient();
        anonymous.BaseAddress = Signal9ApiFactory.HttpsLocalhost;
        anonymous.DefaultRequestHeaders.Add(AgentProtocol.VersionHeader, Signal9ApiFactory.AgentVersion);

        var response = await anonymous.PostAsJsonAsync("api/v1/agents",
            Signal9ApiFactory.Enrollment(token.Secret!, certificate), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(0, (await GetTokenAsync(token)).UseCount);
    }

    [TestMethod]
    public async Task Enrollment_into_a_tenant_at_its_device_limit_is_refused()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso", maxDevices: 1);
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var first = AgentCertificate();
        using var second = AgentCertificate();

        Assert.AreEqual(HttpStatusCode.Created, (await _factory.EnrollAsync(token.Secret!, first)).StatusCode);
        var refused = await _factory.EnrollAsync(token.Secret!, second);

        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.AreEqual(1, (await GetTokenAsync(token)).UseCount);
    }

    [TestMethod]
    public async Task Enrollment_validates_the_machine_facts()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var anonymous = _factory.CreateAnonymousClient();
        anonymous.DefaultRequestHeaders.Add(AgentProtocol.VersionHeader, Signal9ApiFactory.AgentVersion);

        var response = await anonymous.PostAsJsonAsync("api/v1/agents",
            Signal9ApiFactory.Enrollment(token.Secret!, AgentCertificate()) with { MachineName = "" }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, (await GetTokenAsync(token)).UseCount);
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    private CreateTelemetrySampleRequest Sample() => new() { CollectedAt = _factory.Time.GetUtcNow(), CpuPercent = 12 };

    private async Task<AgentDto> GetAgentAsync(Guid agentId) =>
        (await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{agentId}", Signal9Json.Options, Ct))!;

    private async Task<EnrollmentTokenDto> GetTokenAsync(EnrollmentTokenDto token) =>
        (await _client.GetFromJsonAsync<EnrollmentTokenDto>(token.Url.TrimStart('/'), Signal9Json.Options, Ct))!;

    private async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return problem.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }

    /// <summary>Introspects a token as the api resource server, the only party that sees all of its claims.</summary>
    private async Task<JsonElement> IntrospectAsync(string token)
    {
        await _factory.SeedClientAsync("signal9-api");
        using var api = _factory.CreateAnonymousClient();
        var response = await api.PostAsync("connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "signal9-api",
            ["client_secret"] = Signal9ApiFactory.ClientSecret,
            ["token"] = token,
        }), Ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
