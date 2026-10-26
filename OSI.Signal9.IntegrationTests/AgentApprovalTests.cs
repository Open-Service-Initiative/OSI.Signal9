using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using Scopes = OSI.Signal9.Server.Identity.Scopes;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Agent approval (#130, stories 96-97): a tenant can hold new agents for approval, its descendants inherit the hold and
/// can't turn it off, and held agents send heartbeats but receive no work until a Technician approves them.
/// </summary>
[TestClass]
public sealed class AgentApprovalTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public AgentApprovalTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateSuperadministratorClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Tenants_do_not_hold_new_agents_by_default()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        Assert.IsFalse(tenant.HoldsNewAgentsForApproval);
        Assert.IsFalse(tenant.ApprovalHoldInherited);
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        Assert.AreEqual(AgentStatus.Active, agent.Agent.Status);
    }

    [TestMethod]
    public async Task Descendants_inherit_the_hold_and_cannot_turn_it_off()
    {
        var msp = await _factory.CreateTenantAsync(_client, "Msp");
        var client = await _factory.CreateTenantAsync(_client, "Client", parentTenantId: msp.Id);
        var site = await _factory.CreateTenantAsync(_client, "Site", parentTenantId: client.Id);

        var held = await PatchAsync(client.Id, """{ "holdsNewAgentsForApproval": true }""");

        Assert.AreEqual(HttpStatusCode.OK, held.StatusCode, await held.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue((await held.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.HoldsNewAgentsForApproval);
        var inherited = await GetTenantAsync(site.Id);
        Assert.IsTrue(inherited.HoldsNewAgentsForApproval);
        Assert.IsTrue(inherited.ApprovalHoldInherited);
        Assert.IsFalse((await GetTenantAsync(msp.Id)).HoldsNewAgentsForApproval);
        var listed = await _client.GetFromJsonAsync<PagedResult<TenantDto>>($"api/v1/tenants?parentTenantId={client.Id}", Signal9Json.Options, Ct);
        Assert.IsTrue(Assert.ContainsSingle(listed!.Items).ApprovalHoldInherited);

        var turnedOff = await PatchAsync(site.Id, """{ "holdsNewAgentsForApproval": false }""");

        Assert.AreEqual(HttpStatusCode.Conflict, turnedOff.StatusCode);
        Assert.AreEqual("application/problem+json", turnedOff.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue((await GetTenantAsync(site.Id)).HoldsNewAgentsForApproval);
    }

    [TestMethod]
    public async Task Editing_other_fields_of_a_tenant_under_a_hold_leaves_it_inherited()
    {
        var client = await _factory.CreateTenantAsync(_client, "Client");
        var site = await _factory.CreateTenantAsync(_client, "Site", parentTenantId: client.Id);
        (await PatchAsync(client.Id, """{ "holdsNewAgentsForApproval": true }""")).EnsureSuccessStatusCode();

        // The dashboard sends the whole document, inherited hold included.
        (await PatchAsync(site.Id, """{ "name": "Site A", "holdsNewAgentsForApproval": true }""")).EnsureSuccessStatusCode();
        (await PatchAsync(client.Id, """{ "holdsNewAgentsForApproval": false }""")).EnsureSuccessStatusCode();

        Assert.IsFalse((await GetTenantAsync(site.Id)).HoldsNewAgentsForApproval);
    }

    [TestMethod]
    public async Task Setting_the_hold_needs_tenants_write()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var technician = await _factory.CreateApplicationClientAsync(tenant.Id, Scopes.Tenants.Read, Scopes.Devices.Write);

        var response = await technician.PatchAsync($"api/v1/tenants/{tenant.Id}", MergePatch("""{ "holdsNewAgentsForApproval": true }"""), Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsFalse((await GetTenantAsync(tenant.Id)).HoldsNewAgentsForApproval);
    }

    [TestMethod]
    public async Task Agents_enrolling_under_a_hold_await_approval()
    {
        var client = await _factory.CreateTenantAsync(_client, "Client");
        var site = await _factory.CreateTenantAsync(_client, "Site", parentTenantId: client.Id);
        (await PatchAsync(client.Id, """{ "holdsNewAgentsForApproval": true }""")).EnsureSuccessStatusCode();

        using var agent = await _factory.EnrollAgentAsync(site.Id);

        Assert.AreEqual(AgentStatus.AwaitingApproval, agent.Agent.Status);
        var fetched = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{agent.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(AgentStatus.AwaitingApproval, fetched!.Status);
    }

    [TestMethod]
    public async Task Agent_status_is_shown_separately_from_device_status()
    {
        using var agent = await EnrollHeldAsync();
        await HeartbeatAsync(agent);

        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);

        Assert.AreEqual(DeviceStatus.Online, device!.Status);
        Assert.AreEqual(AgentStatus.AwaitingApproval, device.AgentStatus);
    }

    [TestMethod]
    public async Task A_held_agents_heartbeats_are_accepted_but_it_receives_no_work_and_no_doorbell()
    {
        using var agent = await EnrollHeldAsync();
        await using var hub = await ConnectAsync(agent);
        var rung = new TaskCompletionSource();
        hub.On(nameof(IAgentHubClient.WorkAvailable), () => rung.TrySetResult());

        var command = await QueueCommandAsync(agent.DeviceId);

        var heartbeat = await HeartbeatAsync(agent);
        Assert.IsFalse(heartbeat.HasPendingWork);
        var pending = await agent.Client.GetFromJsonAsync<PagedResult<CommandDto>>(
            $"api/v1/agents/{agent.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.IsEmpty(pending!.Items);
        var result = await agent.Client.PutAsJsonAsync(command.Url.TrimStart('/') + "/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
        // A round trip on the connection: any doorbell api rang before it has arrived.
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
        Assert.IsFalse(rung.Task.IsCompleted);
    }

    [TestMethod]
    public async Task Once_a_technician_approves_a_held_agent_work_flows()
    {
        using var agent = await EnrollHeldAsync();
        await using var hub = await ConnectAsync(agent);
        var rung = new TaskCompletionSource();
        hub.On(nameof(IAgentHubClient.WorkAvailable), () => rung.TrySetResult());
        var command = await QueueCommandAsync(agent.DeviceId);
        using var technician = await _factory.CreateApplicationClientAsync(agent.TenantId, Scopes.Devices.Write);

        var approved = await technician.PostAsync($"api/v1/agents/{agent.Id}:approve", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(AgentStatus.Active, (await approved.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!.Status);
        Assert.IsNotNull(approved.Headers.ETag);
        await rung.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.IsTrue((await HeartbeatAsync(agent)).HasPendingWork);
        var pending = await agent.Client.GetFromJsonAsync<PagedResult<CommandDto>>(
            $"api/v1/agents/{agent.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(pending!.Items).Id);
    }

    [TestMethod]
    public async Task Only_an_agent_awaiting_approval_can_be_approved()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var active = await _factory.EnrollAgentAsync(tenant.Id);
        using var held = await EnrollHeldAsync();
        (await _client.PostAsync($"api/v1/agents/{held.Id}:revoke", null, Ct)).EnsureSuccessStatusCode();

        var activeApproved = await _client.PostAsync($"api/v1/agents/{active.Id}:approve", null, Ct);
        var revokedApproved = await _client.PostAsync($"api/v1/agents/{held.Id}:approve", null, Ct);
        var unknown = await _client.PostAsync($"api/v1/agents/{Guid.NewGuid()}:approve", null, Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, activeApproved.StatusCode);
        Assert.AreEqual("application/problem+json", activeApproved.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Conflict, revokedApproved.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        var revoked = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{held.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(AgentStatus.Revoked, revoked!.Status);
    }

    [TestMethod]
    public async Task Approving_needs_devices_write()
    {
        using var agent = await EnrollHeldAsync();
        using var reader = await _factory.CreateApplicationClientAsync(agent.TenantId, Scopes.Devices.Read);

        var response = await reader.PostAsync($"api/v1/agents/{agent.Id}:approve", null, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(AgentStatus.AwaitingApproval,
            (await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{agent.Id}", Signal9Json.Options, Ct))!.Status);
    }

    [TestMethod]
    public async Task Approving_honours_if_match()
    {
        using var agent = await EnrollHeldAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/agents/{agent.Id}:approve");
        request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale\""));

        var response = await _client.SendAsync(request, Ct);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [TestMethod]
    public async Task A_tenant_lists_its_held_agents()
    {
        var client = await _factory.CreateTenantAsync(_client, "Client");
        var site = await _factory.CreateTenantAsync(_client, "Site", parentTenantId: client.Id);
        using var active = await _factory.EnrollAgentAsync(site.Id, "PC01");
        (await PatchAsync(client.Id, """{ "holdsNewAgentsForApproval": true }""")).EnsureSuccessStatusCode();
        using var held = await _factory.EnrollAgentAsync(site.Id, "PC02");

        var page = await _client.GetFromJsonAsync<PagedResult<AgentDto>>(
            $"api/v1/tenants/{client.Id}/agents?status=AwaitingApproval", Signal9Json.Options, Ct);

        var listed = Assert.ContainsSingle(page!.Items);
        Assert.AreEqual(held.Id, listed.Id);
        Assert.AreEqual("PC02", listed.MachineName);
        Assert.AreEqual($"/api/v1/agents/{held.Id}", listed.Url);
    }

    [TestMethod]
    public async Task Hold_changes_and_approvals_are_audited()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        (await PatchAsync(tenant.Id, """{ "holdsNewAgentsForApproval": true }""")).EnsureSuccessStatusCode();
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        (await _client.PostAsync($"api/v1/agents/{agent.Id}:approve", null, Ct)).EnsureSuccessStatusCode();
        (await PatchAsync(tenant.Id, """{ "holdsNewAgentsForApproval": false }""")).EnsureSuccessStatusCode();
        // Edits that don't change the hold aren't hold changes.
        (await PatchAsync(tenant.Id, """{ "name": "Contoso Ltd", "holdsNewAgentsForApproval": false }""")).EnsureSuccessStatusCode();

        var entries = await _client.GetFromJsonAsync<PagedResult<AuditEntryDto>>(
            "api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct);
        var holds = entries!.Items.Where(e => e.Action == AuditActions.TenantApprovalHoldChanged).OrderBy(e => e.OccurredAt).ToList();
        CollectionAssert.AreEquivalent(new[] { "on", "off" }, holds.Select(e => e.Detail).ToArray());
        Assert.IsTrue(holds.All(e => e.TenantId == tenant.Id && e.TargetType == "tenant" && e.TargetId == tenant.Id));
        var approval = Assert.ContainsSingle(entries.Items.Where(e => e.Action == AuditActions.AgentApproved));
        Assert.AreEqual(tenant.Id, approval.TenantId);
        Assert.AreEqual("agent", approval.TargetType);
        Assert.AreEqual(agent.Id, approval.TargetId);
        Assert.AreEqual(agent.DeviceId, approval.DeviceId);
        Assert.AreEqual(AuditOutcome.Succeeded, approval.Outcome);
    }

    private async Task<TestAgent> EnrollHeldAsync()
    {
        var tenant = await _factory.CreateTenantAsync(_client, $"Tenant{Guid.NewGuid():N}");
        (await PatchAsync(tenant.Id, """{ "holdsNewAgentsForApproval": true }""")).EnsureSuccessStatusCode();
        return await _factory.EnrollAgentAsync(tenant.Id);
    }

    private async Task<TenantDto> GetTenantAsync(Guid tenantId) =>
        (await _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenantId}", Signal9Json.Options, Ct))!;

    private static StringContent MergePatch(string patch) =>
        new(patch, System.Text.Encoding.UTF8, "application/merge-patch+json");

    private Task<HttpResponseMessage> PatchAsync(Guid tenantId, string patch) =>
        _client.PatchAsync($"api/v1/tenants/{tenantId}", MergePatch(patch), Ct);

    private async Task<HeartbeatResponse> HeartbeatAsync(TestAgent agent)
    {
        var response = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(Signal9Json.Options, Ct))!;
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    /// <summary>Connects as the agent and returns once the API has placed the connection in the agent's group.</summary>
    private async Task<HubConnection> ConnectAsync(TestAgent agent)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, HubRoutes.Agents.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ =>
                    _factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy);
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(agent.Client.DefaultRequestHeaders.Authorization?.Parameter);
                options.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(agent.Certificate);
                options.Headers[AgentProtocol.VersionHeader] = Signal9ApiFactory.AgentVersion;
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();
        await hub.StartAsync(Ct);
        await Assert.ThrowsExactlyAsync<Microsoft.AspNetCore.SignalR.HubException>(() => hub.InvokeAsync("Ping", Ct));
        return hub;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
