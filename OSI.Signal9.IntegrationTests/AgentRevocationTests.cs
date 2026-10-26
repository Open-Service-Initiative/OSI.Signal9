using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Revoking an agent (#129, story 101): a compromised or retired installation loses access at once, including its live
/// hub connection, and its device then requires approval for its next agent.
/// </summary>
[TestClass]
public sealed class AgentRevocationTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public AgentRevocationTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Revoking_an_agent_makes_its_next_call_and_token_request_fail()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);

        var revoked = await _client.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));
        var dto = (await revoked.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(_factory.Time.GetUtcNow(), dto.RevokedAt);
        var heartbeat = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Unauthorized, heartbeat.StatusCode);
        var token = await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate);
        Assert.AreEqual(HttpStatusCode.BadRequest, token.StatusCode);
        Assert.AreEqual(AgentErrors.Revoked, (await token.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Revoking_an_agent_closes_its_hub_connection_and_it_cannot_reconnect()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        await using var hub = await ConnectAsync(agent);
        var closed = new TaskCompletionSource();
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        (await _client.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct)).EnsureSuccessStatusCode();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var again = BuildHub(agent);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => again.StartAsync(Ct));
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [TestMethod]
    public async Task Deleting_a_device_deletes_its_agents_clients_and_closes_their_hub_connections()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        await using var hub = await ConnectAsync(agent);
        var closed = new TaskCompletionSource();
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var deleted = await _client.DeleteAsync($"api/v1/devices/{agent.DeviceId}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var heartbeat = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Unauthorized, heartbeat.StatusCode);
        var token = await _factory.RequestAgentTokenAsync(agent.Id, agent.Certificate);
        Assert.AreEqual("invalid_client", (await token.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task After_revocation_the_device_has_no_active_agent_and_requires_approval_for_its_next()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        var before = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);
        Assert.IsFalse(before!.RequiresAgentApproval);

        (await _client.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct)).EnsureSuccessStatusCode();

        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);
        Assert.IsTrue(device!.RequiresAgentApproval);
        Assert.IsNull(device.ActiveAgentId);
        Assert.AreEqual(DeviceStatus.Offline, device.Status);
        var revoked = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{agent.Id}", Signal9Json.Options, Ct);
        Assert.IsFalse(revoked!.IsActive);
        Assert.IsNotNull(revoked.RevokedAt);
    }

    [TestMethod]
    public async Task Revocation_is_audited_at_the_devices_tenant()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);

        (await _client.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct)).EnsureSuccessStatusCode();

        var entries = await _client.GetFromJsonAsync<PagedResult<AuditEntryDto>>(
            "api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct);
        var revoked = Assert.ContainsSingle(entries!.Items.Where(e => e.Action == AuditActions.AgentRevoked));
        Assert.AreEqual(tenant.Id, revoked.TenantId);
        Assert.AreEqual("agent", revoked.TargetType);
        Assert.AreEqual(agent.Id, revoked.TargetId);
        Assert.AreEqual(agent.DeviceId, revoked.DeviceId);
        Assert.AreEqual(AuditOutcome.Succeeded, revoked.Outcome);
    }

    [TestMethod]
    public async Task A_revoked_agent_cannot_be_revoked_again()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        (await _client.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct)).EnsureSuccessStatusCode();

        var again = await _client.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.AreEqual("application/problem+json", again.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Revoking_an_unknown_agent_is_404()
    {
        var response = await _client.PostAsync($"api/v1/agents/{Guid.NewGuid()}:revoke", null, Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Revoking_needs_devices_write()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        using var reader = await _factory.CreateClientAsync("devices.read");

        var response = await reader.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsNull((await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{agent.Id}", Signal9Json.Options, Ct))!.RevokedAt);
    }

    [TestMethod]
    public async Task Revoking_honours_if_match()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        var current = (await _client.GetAsync($"api/v1/agents/{agent.Id}", Ct)).Headers.ETag!;

        using var stale = new HttpRequestMessage(HttpMethod.Post, $"api/v1/agents/{agent.Id}:revoke");
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale\""));
        var refused = await _client.SendAsync(stale, Ct);
        using var matching = new HttpRequestMessage(HttpMethod.Post, $"api/v1/agents/{agent.Id}:revoke");
        matching.Headers.IfMatch.Add(current);
        var accepted = await _client.SendAsync(matching, Ct);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        Assert.IsNotNull(accepted.Headers.ETag);
    }

    /// <summary>Connects as the agent and returns once the API has placed the connection in the agent's group.</summary>
    private async Task<HubConnection> ConnectAsync(TestAgent agent)
    {
        var hub = BuildHub(agent);
        await hub.StartAsync(Ct);
        // The hub handles a connection's messages only after OnConnectedAsync, so this answer means it's registered.
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
        return hub;
    }

    private HubConnection BuildHub(TestAgent agent) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, HubRoutes.Agents.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ =>
                    _factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy);
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(agent.Client.DefaultRequestHeaders.Authorization!.Parameter);
                options.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(agent.Certificate);
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
