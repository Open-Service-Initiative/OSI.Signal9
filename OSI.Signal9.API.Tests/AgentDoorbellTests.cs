using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// The agent hub is a doorbell (ADR-0009): it only tells an agent it has work, and the agent fetches the work over REST.
/// </summary>
[TestClass]
public sealed class AgentDoorbellTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public AgentDoorbellTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task The_agent_hub_accepts_no_calls_from_agents()
    {
        var agent = await RegisterAsync("PC01");
        await using var hub = await ConnectAsync(agent.Id);

        var names = typeof(AgentHub).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).Append("CommandQueued").Distinct();
        foreach (var name in names)
            await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync(name, Ct), name);
    }

    [TestMethod]
    public void The_doorbell_carries_no_content()
    {
        foreach (var method in typeof(IAgentHubClient).GetMethods())
            Assert.IsEmpty(method.GetParameters(), method.Name);
    }

    [TestMethod]
    public async Task Queuing_a_command_rings_the_doorbell_of_the_devices_active_agent()
    {
        var agent = await RegisterAsync("PC01");
        await using var hub = await ConnectAsync(agent.Id);
        var rung = new TaskCompletionSource();
        hub.On(nameof(IAgentHubClient.WorkAvailable), () => rung.TrySetResult());

        await QueueCommandAsync(agent.DeviceId);

        await rung.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [TestMethod]
    public async Task An_agent_lists_its_devices_pending_commands_oldest_first_with_their_canonical_urls()
    {
        var agent = await RegisterAsync("PC01");
        var first = await QueueCommandAsync(agent.DeviceId);
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var second = await QueueCommandAsync(agent.DeviceId);
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var cancelled = await QueueCommandAsync(agent.DeviceId);
        (await _client.PostAsync($"api/v1/devices/{agent.DeviceId}/commands/{cancelled.Id}:cancel", null, Ct)).EnsureSuccessStatusCode();
        var other = await RegisterAsync("PC02");
        await QueueCommandAsync(other.DeviceId);

        var response = await _client.GetAsync($"api/v1/agents/{agent.Id}/pending-commands", Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<PagedResult<CommandDto>>(Signal9Json.Options, Ct))!;
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, page.Items.Select(c => c.Id).ToArray());
        Assert.AreEqual($"/api/v1/devices/{agent.DeviceId}/commands/{first.Id}", page.Items[0].Url);
    }

    [TestMethod]
    public async Task Listing_pending_commands_is_refused_for_an_unknown_or_replaced_agent()
    {
        var replaced = await RegisterAsync("PC01");
        await Signal9ApiFactory.RegisterAsync(_client, replaced.TenantId, "PC01");

        var unknown = await _client.GetAsync($"api/v1/agents/{Guid.NewGuid()}/pending-commands", Ct);
        var refused = await _client.GetAsync($"api/v1/agents/{replaced.Id}/pending-commands", Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [TestMethod]
    public async Task Queued_commands_carry_their_canonical_url()
    {
        var agent = await RegisterAsync("PC01");

        var command = await QueueCommandAsync(agent.DeviceId);

        Assert.AreEqual($"/api/v1/devices/{agent.DeviceId}/commands/{command.Id}", command.Url);
    }

    [TestMethod]
    public async Task A_heartbeat_response_says_whether_work_is_waiting()
    {
        var agent = await RegisterAsync("PC01");

        var idle = await HeartbeatAsync(agent.Id);
        var command = await QueueCommandAsync(agent.DeviceId);
        var waiting = await HeartbeatAsync(agent.Id);
        (await _client.PutAsJsonAsync(command.Url.TrimStart('/') + "/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct)).EnsureSuccessStatusCode();
        var done = await HeartbeatAsync(agent.Id);

        Assert.IsFalse(idle.HasPendingWork);
        Assert.IsTrue(waiting.HasPendingWork);
        Assert.IsFalse(done.HasPendingWork);
    }

    private async Task<HeartbeatResponse> HeartbeatAsync(Guid agentId)
    {
        var response = await _client.PutAsJsonAsync($"api/v1/agents/{agentId}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(Signal9Json.Options, Ct))!;
    }

    /// <summary>Connects as the agent and returns once the API has placed the connection in the agent's group.</summary>
    private async Task<HubConnection> ConnectAsync(Guid agentId)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, $"{HubRoutes.Agents.TrimStart('/')}?agentId={agentId}"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(_client.DefaultRequestHeaders.Authorization?.Parameter);
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();
        await hub.StartAsync(Ct);
        // The hub handles a connection's messages only after OnConnectedAsync, so this answer means the agent joined its group.
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
        return hub;
    }

    private async Task<AgentDto> RegisterAsync(string machineName)
    {
        var tenant = await _factory.CreateTenantAsync(_client, $"Tenant{Guid.NewGuid():N}");
        var response = await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, machineName);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
