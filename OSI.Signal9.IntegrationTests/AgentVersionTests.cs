using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The agent version check: every agent request names its version (<see cref="AgentProtocol.VersionHeader"/>), and api
/// refuses one outside the deployment's supported range at enrollment, the token endpoint, its own endpoints and the hub.
/// </summary>
[TestClass]
public sealed class AgentVersionTests : IDisposable
{
    private const string TooOld = "0.0.1";

    private readonly Signal9ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public AgentVersionTests(TestContext testContext)
    {
        _testContext = testContext;
        _factory = testContext.TestName == nameof(The_supported_range_is_configurable)
            ? new Signal9ApiFactory(settings: new Dictionary<string, string?>
            {
                ["AgentVersions:Minimum"] = "1.0.0",
                ["AgentVersions:Maximum"] = "2.0.0",
            })
            : new Signal9ApiFactory();
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task By_default_the_deployment_supports_agents_of_its_own_version()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);

        Assert.AreEqual(Signal9ApiFactory.AgentVersion, agent.Agent.AgentVersion);
        var heartbeat = await agent.Client.PutAsJsonAsync(AgentProtocol.Routes.HeartbeatOf(agent.Id), new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode, await heartbeat.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Enrollment_by_an_agent_that_names_no_version_is_refused()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);
        using var anonymous = _factory.CreateAnonymousClient();

        var response = await anonymous.PostAsJsonAsync(AgentProtocol.Routes.Enroll,
            Signal9ApiFactory.Enrollment(token.Secret!, TestCertificates.AgentCertificate()), Signal9Json.Options, Ct);

        await AssertRefusedAsync(response);
    }

    [TestMethod]
    [DataRow("not-a-version", DisplayName = "Malformed")]
    [DataRow(TooOld, DisplayName = "Too old")]
    [DataRow("99.0.0", DisplayName = "Newer than api")]
    public async Task Enrollment_by_an_unsupported_agent_is_refused(string version)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id);

        var response = await _factory.EnrollAsync(token.Secret!, TestCertificates.AgentCertificate(), version: version);

        await AssertRefusedAsync(response);
    }

    [TestMethod]
    public async Task A_token_request_from_an_unsupported_agent_is_refused_with_an_oauth_error()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);

        var response = await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate, version: TooOld);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(AgentProtocol.UnsupportedVersion.Error,
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task The_agent_s_own_endpoints_refuse_an_unsupported_agent()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var request = new HttpRequestMessage(HttpMethod.Put, AgentProtocol.Routes.HeartbeatOf(agent.Id))
        {
            Content = JsonContent.Create(new HeartbeatRequest(), options: Signal9Json.Options),
        };
        request.Headers.Remove(AgentProtocol.VersionHeader);
        request.Headers.Add(AgentProtocol.VersionHeader, TooOld);

        var response = await agent.Client.SendAsync(request, Ct);

        await AssertRefusedAsync(response);
    }

    [TestMethod]
    public async Task The_hub_refuses_an_unsupported_agent()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, HubRoutes.Agents.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ =>
                    _factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy);
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(agent.Client.DefaultRequestHeaders.Authorization!.Parameter);
                options.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(agent.Certificate);
                options.Headers[AgentProtocol.VersionHeader] = TooOld;
            })
            .Build();

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync(Ct));

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [TestMethod]
    public async Task The_supported_range_is_configurable()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var token = await _factory.IssueEnrollmentTokenAsync(tenant.Id, maxUses: 5);

        var inRange = await _factory.EnrollAsync(token.Secret!, TestCertificates.AgentCertificate(), version: "1.5.0-beta.2");
        var current = await _factory.EnrollAsync(token.Secret!, TestCertificates.AgentCertificate(), "PC02");

        Assert.AreEqual(HttpStatusCode.Created, inRange.StatusCode, await inRange.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual("1.5.0-beta.2", (await inRange.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!.AgentVersion);
        var refusal = await AssertRefusedAsync(current);
        Assert.AreEqual("1.0.0", refusal.GetProperty("minimumVersion").GetString());
        Assert.AreEqual("2.0.0", refusal.GetProperty("maximumVersion").GetString());
    }

    [TestMethod]
    public void A_range_whose_minimum_is_newer_than_its_maximum_fails_at_startup()
    {
        using var misconfigured = new Signal9ApiFactory(settings: new Dictionary<string, string?>
        {
            ["AgentVersions:Minimum"] = "2.0.0",
            ["AgentVersions:Maximum"] = "1.0.0",
        });

        var failure = Assert.Throws<OptionsValidationException>(() => misconfigured.CreateClient());

        StringAssert.Contains(failure.Message, "newer than the maximum");
    }

    private async Task<JsonElement> AssertRefusedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.AreEqual(AgentProtocol.UnsupportedVersion.ProblemType, problem.GetProperty("type").GetString());
        return problem;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
