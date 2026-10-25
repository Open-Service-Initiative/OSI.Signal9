using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
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

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The agent hub is a doorbell (ADR-0009): it only tells an agent it has work, and the agent fetches the work over REST.
/// It needs the <c>agent</c> scope with a certificate-bound token, and takes the agent's identity from that token.
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
        using var agent = await EnrollAsync("PC01");
        await using var hub = await ConnectAsync(agent);

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
        using var agent = await EnrollAsync("PC01");
        await using var hub = await ConnectAsync(agent);
        var rung = new TaskCompletionSource();
        hub.On(nameof(IAgentHubClient.WorkAvailable), () => rung.TrySetResult());

        await QueueCommandAsync(agent.DeviceId);

        await rung.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [TestMethod]
    public async Task An_agent_hears_only_its_own_doorbell_whatever_agent_it_names()
    {
        using var agent = await EnrollAsync("PC01");
        using var other = await EnrollAsync("PC02");
        await using var hub = await ConnectAsync(agent, query: $"?agentId={other.Id}");
        var rings = 0;
        var rung = new TaskCompletionSource();
        hub.On(nameof(IAgentHubClient.WorkAvailable), () =>
        {
            Interlocked.Increment(ref rings);
            rung.TrySetResult();
        });

        await QueueCommandAsync(other.DeviceId);
        await QueueCommandAsync(agent.DeviceId);

        await rung.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(200, Ct);
        Assert.AreEqual(1, rings);
    }

    [TestMethod]
    public async Task The_agent_hub_refuses_a_token_that_belongs_to_no_agent()
    {
        using var application = await _factory.CreateClientAsync(OSI.Signal9.Server.Identity.Scopes.Agent);
        await using var hub = Build(application.DefaultRequestHeaders.Authorization!.Parameter!, certificate: null);

        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await hub.StartAsync(Ct);
            await hub.InvokeAsync("Ping", Ct);
        });
    }

    [TestMethod]
    public async Task The_agent_hub_refuses_an_agent_token_without_its_certificate()
    {
        using var agent = await EnrollAsync("PC01");
        using var thief = TestCertificates.AgentCertificate();
        await using var withoutCertificate = Build(agent.Client.DefaultRequestHeaders.Authorization!.Parameter!, certificate: null);
        await using var withOtherCertificate = Build(agent.Client.DefaultRequestHeaders.Authorization!.Parameter!, thief);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => withoutCertificate.StartAsync(Ct));
        var stolen = await Assert.ThrowsAsync<HttpRequestException>(() => withOtherCertificate.StartAsync(Ct));

        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, stolen.StatusCode);
    }

    [TestMethod]
    public async Task An_agent_lists_its_devices_pending_commands_oldest_first_with_their_canonical_urls()
    {
        using var agent = await EnrollAsync("PC01");
        var first = await QueueCommandAsync(agent.DeviceId);
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var second = await QueueCommandAsync(agent.DeviceId);
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var cancelled = await QueueCommandAsync(agent.DeviceId);
        (await _client.PostAsync($"api/v1/devices/{agent.DeviceId}/commands/{cancelled.Id}:cancel", null, Ct)).EnsureSuccessStatusCode();
        using var other = await EnrollAsync("PC02");
        await QueueCommandAsync(other.DeviceId);

        var response = await agent.Client.GetAsync($"api/v1/agents/{agent.Id}/pending-commands", Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<PagedResult<CommandDto>>(Signal9Json.Options, Ct))!;
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, page.Items.Select(c => c.Id).ToArray());
        Assert.AreEqual($"/api/v1/devices/{agent.DeviceId}/commands/{first.Id}", page.Items[0].Url);
    }

    [TestMethod]
    public async Task Queued_commands_carry_their_canonical_url()
    {
        using var agent = await EnrollAsync("PC01");

        var command = await QueueCommandAsync(agent.DeviceId);

        Assert.AreEqual($"/api/v1/devices/{agent.DeviceId}/commands/{command.Id}", command.Url);
    }

    [TestMethod]
    public async Task A_heartbeat_response_says_whether_work_is_waiting()
    {
        using var agent = await EnrollAsync("PC01");

        var idle = await HeartbeatAsync(agent);
        var command = await QueueCommandAsync(agent.DeviceId);
        var waiting = await HeartbeatAsync(agent);
        (await agent.Client.PutAsJsonAsync(command.Url.TrimStart('/') + "/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct)).EnsureSuccessStatusCode();
        var done = await HeartbeatAsync(agent);

        Assert.IsFalse(idle.HasPendingWork);
        Assert.IsTrue(waiting.HasPendingWork);
        Assert.IsFalse(done.HasPendingWork);
    }

    private async Task<HeartbeatResponse> HeartbeatAsync(TestAgent agent)
    {
        var response = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(Signal9Json.Options, Ct))!;
    }

    /// <summary>Connects as the agent and returns once the API has placed the connection in the agent's group.</summary>
    private async Task<HubConnection> ConnectAsync(TestAgent agent, string query = "")
    {
        var hub = Build(agent.Client.DefaultRequestHeaders.Authorization!.Parameter!, agent.Certificate, query);
        await hub.StartAsync(Ct);
        // The hub handles a connection's messages only after OnConnectedAsync, so this answer means the agent joined its group.
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
        return hub;
    }

    /// <summary>A hub connection behind Container Apps' ingress, which forwards <paramref name="certificate"/>.</summary>
    private HubConnection Build(string accessToken, X509Certificate2? certificate, string query = "") =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, HubRoutes.Agents.TrimStart('/') + query), options =>
            {
                options.HttpMessageHandlerFactory = _ =>
                    _factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy);
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                if (certificate is not null)
                    options.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(certificate);
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();

    private async Task<TestAgent> EnrollAsync(string machineName)
    {
        var tenant = await _factory.CreateTenantAsync(_client, $"Tenant{Guid.NewGuid():N}");
        return await _factory.EnrollAgentAsync(tenant.Id, machineName);
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
