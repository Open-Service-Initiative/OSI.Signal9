using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Agent;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// End to end: the agent's session against the API in memory. Work reaches the agent by doorbell then fetch, by a fetch
/// on every reconnect, and by a fetch when a heartbeat says work is waiting.
/// </summary>
[TestClass]
public sealed class AgentDeliveryTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly RequestLog _agentRequests = new();
    private readonly FakeTimeProvider _agentTime = new();
    private readonly AgentOptions _agentOptions = new()
    {
        ApiBaseUrl = Signal9ApiFactory.HttpsLocalhost,
        TenantId = Guid.NewGuid(),
        HeartbeatInterval = TimeSpan.FromSeconds(30),
        TelemetryInterval = TimeSpan.FromHours(1),
    };
    private readonly CancellationTokenSource _stop;
    private readonly TestContext _testContext;
    private Network _network = null!;
    private Task _session = Task.CompletedTask;

    public AgentDeliveryTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_queued_command_rings_the_doorbell_and_the_agent_fetches_it_and_reports_its_result()
    {
        var agent = await StartAgentAsync();

        var command = await QueueCommandAsync(agent.DeviceId);

        var finished = await WaitUntilFinishedAsync(command);
        Assert.AreEqual(CommandStatus.Completed, finished.Status);
        Assert.IsNotNull(finished.Output);
    }

    [TestMethod]
    public async Task After_missing_doorbells_while_disconnected_the_agent_fetches_its_work_on_reconnect()
    {
        var agent = await StartAgentAsync();
        await _network.GoOfflineAsync();

        var first = await QueueCommandAsync(agent.DeviceId);
        var second = await QueueCommandAsync(agent.DeviceId);
        Assert.AreEqual(CommandStatus.Pending, (await GetAsync(first)).Status);

        _network.GoOnline();

        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(first)).Status);
        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(second)).Status);
    }

    [TestMethod]
    public async Task When_a_heartbeat_says_work_is_waiting_the_agent_fetches_it()
    {
        var agent = await StartAgentAsync();
        await _network.GoOfflineAsync();
        var command = await QueueCommandAsync(agent.DeviceId);

        _agentTime.Advance(_agentOptions.HeartbeatInterval);

        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(command)).Status);
        Assert.AreEqual(HubConnectionState.Reconnecting, _network.Hub!.State);
    }

    /// <summary>
    /// Registers an agent and starts its session, returning once it has connected, fetched its work and sent its first
    /// heartbeat, so the only things left to make it fetch again are the doorbell, a reconnect and the heartbeat timer.
    /// </summary>
    private async Task<AgentDto> StartAgentAsync()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var response = await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, "PC01");
        response.EnsureSuccessStatusCode();
        var agent = (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;

        _network = new Network(_factory.Server, _client.DefaultRequestHeaders.Authorization!);
        var api = new AgentApiClient(_factory.CreateDefaultClient(_agentRequests));
        var options = Options.Create(_agentOptions);
        var telemetry = new TelemetryCollector(_agentTime, NullLogger<TelemetryCollector>.Instance);
        var executor = new CommandExecutor(new SystemInfoProvider(options), telemetry, NullLogger<CommandExecutor>.Instance);
        var session = new AgentSession(api, executor, telemetry, _network.Connect, _agentTime, options,
            NullLogger<AgentSession>.Instance);
        _session = session.RunAsync(agent, _stop.Token);

        await WaitUntilAsync(() => Task.FromResult(
            _network.Hub?.State == HubConnectionState.Connected &&
            _agentRequests.Completed($"/api/v1/agents/{agent.Id}/pending-commands") > 0 &&
            _agentRequests.Completed($"/api/v1/agents/{agent.Id}/heartbeat") > 0));
        return agent;
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<CommandDto> GetAsync(CommandDto command) =>
        (await _client.GetFromJsonAsync<CommandDto>(command.Url.TrimStart('/'), Signal9Json.Options, Ct))!;

    private async Task<CommandDto> WaitUntilFinishedAsync(CommandDto command)
    {
        CommandDto current = command;
        await WaitUntilAsync(async () => (current = await GetAsync(command)).Status != CommandStatus.Pending);
        return current;
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        while (!await condition())
        {
            if (_session.IsCompleted)
                await _session; // Surfaces why the agent's session ended.
            await Task.Delay(20, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _session; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>The agent's connection to the hub over an in-memory WebSocket that the test can cut.</summary>
    private sealed class Network(TestServer server, AuthenticationHeaderValue authorization)
    {
        private volatile bool _offline;
        private WebSocket? _socket;

        public HubConnection? Hub { get; private set; }

        public HubConnection Connect(Guid agentId) => Hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, $"{HubRoutes.Agents.TrimStart('/')}?agentId={agentId}"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    if (_offline)
                        throw new WebSocketException("The network is down.");
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Authorization = authorization.ToString();
                    return _socket = await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .WithAutomaticReconnect(new RetryQuickly())
            .Build();

        /// <summary>Cuts the connection and keeps it down, returning once the agent knows.</summary>
        public async Task GoOfflineAsync()
        {
            _offline = true;
            _socket?.Abort();
            using var timeout = new CancellationTokenSource(Timeout);
            while (Hub!.State != HubConnectionState.Reconnecting)
                await Task.Delay(20, timeout.Token);
        }

        public void GoOnline() => _offline = false;
    }

    private sealed class RetryQuickly : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(Microsoft.AspNetCore.SignalR.Client.RetryContext retryContext) => TimeSpan.FromMilliseconds(50);
    }

    /// <summary>Counts the agent's completed REST requests by path.</summary>
    private sealed class RequestLog : DelegatingHandler
    {
        private readonly List<string> _completed = [];

        public int Completed(string path)
        {
            lock (_completed)
                return _completed.Count(p => p == path);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            lock (_completed)
                _completed.Add(request.RequestUri!.AbsolutePath);
            return response;
        }
    }
}
