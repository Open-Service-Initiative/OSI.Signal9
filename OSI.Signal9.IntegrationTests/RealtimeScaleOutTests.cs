using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Self-hosted real-time with api scaled out (story 117, ADR-0009): two api replicas share a Redis backplane, the hubs
/// take WebSockets only, and a client may negotiate at one replica and open its socket at another, as it would behind
/// a load balancer without sticky sessions. Doorbells, dashboard events and closes reach a connection whichever replica
/// holds it.
/// </summary>
[TestClass]
public sealed class RealtimeScaleOutTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly TestContext _testContext;
    private Signal9ApiFactory _factory = null!;
    private WebApplicationFactory<ApiHost> _replica = null!;
    private HttpClient _client = null!;

    public RealtimeScaleOutTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestInitialize]
    public async Task StartAsync()
    {
        _factory = new Signal9ApiFactory(settings: await RedisContainer.BackplaneSettingsAsync());
        _replica = _factory.CreateApiReplica();
        // The fixture's own client stands in for a Superadministrator, on the first replica.
        _client = _factory.CreateSuperadministratorClient();
    }

    [TestMethod]
    public async Task Scaled_out_hubs_offer_WebSockets_only()
    {
        var person = await _factory.AssignedPersonAsync();
        using var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.PersonTokenAsync(person.User));

        var negotiate = await client.PostAsync($"{HubRoutes.Dashboard.TrimStart('/')}/negotiate?negotiateVersion=1", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, negotiate.StatusCode);
        var transports = (await negotiate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("availableTransports")
            .EnumerateArray().Select(t => t.GetProperty("transport").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "WebSockets" }, transports);
    }

    [TestMethod]
    public async Task A_doorbell_rung_at_one_replica_reaches_an_agent_connected_to_another()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        var rung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = BuildAgentHub(agent);
        hub.On(nameof(IAgentHubClient.WorkAvailable), () => rung.TrySetResult());
        await StartAsync(hub);

        await QueueCommandAsync(agent.DeviceId);

        await rung.Task.WaitAsync(Timeout, Ct);
    }

    [TestMethod]
    public async Task A_dashboard_event_raised_at_one_replica_reaches_a_dashboard_connected_to_another()
    {
        var person = await _factory.AssignedPersonAsync();
        using var agent = await _factory.EnrollAgentAsync(person.TenantId);
        var hub = BuildDashboardHub(await _factory.PersonTokenAsync(person.User));
        await using var dashboard = new DashboardListener(hub, Ct);
        await StartAsync(hub);

        var command = await QueueCommandAsync(agent.DeviceId);

        await dashboard.WaitForCommandAsync(command.Id);
    }

    [TestMethod]
    public async Task Revoking_an_agent_at_one_replica_closes_its_hub_connection_at_another()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = BuildAgentHub(agent);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await StartAsync(hub);

        var revoked = await _client.PostAsync($"api/v1/agents/{agent.Id}:revoke", null, Ct);
        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));

        await NextAccessCheckAsync(closed.Task, new HubAccessOptions().AccessCheckInterval);
    }

    [TestMethod]
    public async Task Removing_a_persons_access_at_one_replica_closes_their_dashboard_connection_at_another()
    {
        var person = await _factory.AssignedPersonAsync();
        var hub = BuildDashboardHub(await _factory.PersonTokenAsync(person.User));
        await using var dashboard = new DashboardListener(hub, Ct);
        await StartAsync(hub);

        var removed = await _client.DeleteAsync(person.Assignment, Ct);
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode, await removed.Content.ReadAsStringAsync(Ct));

        await NextAccessCheckAsync(dashboard.Closed, new HubAccessOptions().AccessCheckInterval);
    }

    /// <summary>Moves the shared clock on to the next access check, and waits for <paramref name="closed"/>.</summary>
    private async Task NextAccessCheckAsync(Task closed, TimeSpan interval)
    {
        _factory.Time.Advance(interval);
        await closed.SignalledAsync("the connection closing");
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    /// <summary>Starts the connection and returns once the hub has handled its connect (and joined its groups).</summary>
    private async Task StartAsync(HubConnection hub)
    {
        await hub.StartAsync(Ct);
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
    }

    private HubConnection BuildAgentHub(TestAgent agent) =>
        Build(HubRoutes.Agents, agent.Client.DefaultRequestHeaders.Authorization!.Parameter!, agent.Certificate);

    private HubConnection BuildDashboardHub(string token) => Build(HubRoutes.Dashboard, token, certificate: null);

    /// <summary>
    /// A client of the hub at <paramref name="path"/> behind a load balancer without sticky sessions: it negotiates at
    /// the first replica and opens its WebSocket at the second, both behind Container Apps' ingress.
    /// </summary>
    private HubConnection Build(string path, string token, X509Certificate2? certificate) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ =>
                    _factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy);
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                if (certificate is not null)
                    options.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(certificate);
                options.Headers[AgentProtocol.VersionHeader] = Signal9ApiFactory.AgentVersion;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = _replica.Server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        request.HttpContext.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy;
                        request.Headers.Authorization = $"Bearer {token}";
                        request.Headers[AgentProtocol.VersionHeader] = Signal9ApiFactory.AgentVersion;
                        if (certificate is not null)
                            request.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(certificate);
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }
}
