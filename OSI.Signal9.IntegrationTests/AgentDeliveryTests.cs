using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Agent;
using OSI.Signal9.Agent.Identity;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// End to end: the real agent enrolls with an enrollment token, authenticates with its certificate and runs its session
/// against the API in memory. Work reaches the agent by doorbell then fetch, by a fetch on every reconnect, and by a
/// fetch when a heartbeat says work is waiting.
/// </summary>
[TestClass]
public sealed class AgentDeliveryTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Signal9ApiFactory _factory;
    private HttpClient _client;
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), $"signal9-agents-{Guid.NewGuid():N}");
    private readonly InMemoryKeyStore _keys = new();
    private readonly FakeTimeProvider _agentTime;
    private readonly CancellationTokenSource _stop;
    private readonly TestContext _testContext;
    private readonly List<RunningAgent> _agents = [];

    public AgentDeliveryTests(TestContext testContext)
    {
        _testContext = testContext;
        // SignalR closes a connection when its token expires by the real clock, so the expiry test starts the hosts'
        // clock just short of a token lifetime ago: its first tokens expire for real a few seconds in.
        _factory = testContext.TestName switch
        {
            nameof(When_its_token_expires_the_hub_closes_and_the_agent_reconnects_with_a_fresh_token) =>
                new Signal9ApiFactory(start: DateTimeOffset.UtcNow - AuthorizationServer.AccessTokenLifetime + TimeSpan.FromSeconds(10)),
            nameof(An_agent_whose_version_the_deployment_no_longer_supports_stops_instead_of_retrying) =>
                new Signal9ApiFactory(settings: new Dictionary<string, string?>
                {
                    ["AgentVersions:Minimum"] = "99.0.0",
                    ["AgentVersions:Maximum"] = "99.0.0",
                }),
            _ => new Signal9ApiFactory(),
        };
        _agentTime = new FakeTimeProvider(_factory.Time.GetUtcNow());
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
        await agent.Network.GoOfflineAsync();

        var first = await QueueCommandAsync(agent.DeviceId);
        var second = await QueueCommandAsync(agent.DeviceId);
        Assert.AreEqual(CommandStatus.Pending, (await GetAsync(first)).Status);

        agent.Network.GoOnline();

        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(first)).Status);
        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(second)).Status);
    }

    [TestMethod]
    public async Task When_a_heartbeat_says_work_is_waiting_the_agent_fetches_it()
    {
        var agent = await StartAgentAsync();
        await agent.Network.GoOfflineAsync();
        var command = await QueueCommandAsync(agent.DeviceId);

        _agentTime.Advance(agent.Options.HeartbeatInterval);

        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(command)).Status);
        Assert.AreEqual(HubConnectionState.Reconnecting, agent.Network.Hub!.State);
    }

    [TestMethod]
    public async Task When_its_token_expires_the_hub_closes_and_the_agent_reconnects_with_a_fresh_token()
    {
        var agent = await StartAgentAsync();
        var connections = agent.Network.Connections;

        // Both clocks catch up with the real one, which reaches the token's expiry within seconds: the hub closes the
        // connection, and the agent reconnects with a new token.
        _factory.Time.Advance(AuthorizationServer.AccessTokenLifetime);
        _agentTime.Advance(AuthorizationServer.AccessTokenLifetime);
        _client.Dispose();
        _client = await _factory.CreateClientAsync(Signal9ApiFactory.GrantableScopes);
        await WaitUntilAsync(() => Task.FromResult(
            agent.Network.Connections > connections && agent.Network.Hub!.State == HubConnectionState.Connected));

        var command = await QueueCommandAsync(agent.DeviceId);
        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(command)).Status);
        Assert.IsGreaterThanOrEqualTo(2, agent.Network.DistinctTokens);
    }

    [TestMethod]
    public async Task Two_instances_on_one_machine_enroll_into_different_root_trees_and_run_independently()
    {
        var contoso = await StartAgentAsync("contoso");
        var fabrikam = await StartAgentAsync("fabrikam");

        var forContoso = await QueueCommandAsync(contoso.DeviceId);
        var forFabrikam = await QueueCommandAsync(fabrikam.DeviceId);

        Assert.AreNotEqual(contoso.Agent.Id, fabrikam.Agent.Id);
        Assert.AreNotEqual(contoso.Agent.TenantId, fabrikam.Agent.TenantId);
        Assert.AreNotEqual(contoso.DeviceId, fabrikam.DeviceId);
        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(forContoso)).Status);
        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(forFabrikam)).Status);
    }

    [TestMethod]
    public async Task A_restarted_instance_runs_as_the_agent_it_enrolled_as_without_enrolling_again()
    {
        var first = await StartAgentAsync();
        await first.StopAsync();

        var restarted = await StartAgentAsync(enrollmentToken: null);

        Assert.AreEqual(first.Agent.Id, restarted.Agent.Id);
        var command = await QueueCommandAsync(restarted.DeviceId);
        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(command)).Status);
    }

    [TestMethod]
    public async Task When_its_certificate_nears_expiry_the_agent_rotates_its_key_and_keeps_working()
    {
        var agent = await StartAgentAsync();
        var original = agent.Identities.Load()!.Certificate;
        var connections = agent.Network.Connections;

        // Both clocks reach the point where the certificate falls due; the session ends and the agent rotates.
        var due = AgentIdentityStore.RenewalDue(original) - _agentTime.GetUtcNow();
        _factory.Time.Advance(due);
        _agentTime.Advance(due);
        _client.Dispose();
        _client = await _factory.CreateClientAsync(Signal9ApiFactory.GrantableScopes);
        await WaitUntilAsync(() => Task.FromResult(
            agent.Identities.Load()!.Certificate.Thumbprint != original.Thumbprint
            && agent.Network.Connections > connections
            && agent.Network.Hub!.State == HubConnectionState.Connected));

        var rotated = agent.Identities.Load()!.Certificate;
        var registered = await _client.GetFromJsonAsync<AgentCertificateDto>($"api/v1/agents/{agent.Agent.Id}/certificate", Signal9Json.Options, Ct);
        Assert.AreEqual(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(SHA256.HashData(rotated.RawData)), registered!.Thumbprint);
        Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, (await _factory.RequestAgentTokenAsync(agent.Agent.Id, original)).StatusCode);
        var command = await QueueCommandAsync(agent.DeviceId);
        Assert.AreEqual(CommandStatus.Completed, (await WaitUntilFinishedAsync(command)).Status);
    }

    [TestMethod]
    public async Task Heartbeats_report_the_machine_s_current_address()
    {
        var agent = await StartAgentAsync();

        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);

        Assert.AreEqual("10.0.0.3", device!.IpAddress);
        Assert.AreEqual(OsFamily.Linux, device.OsFamily);
    }

    [TestMethod]
    public async Task An_agent_whose_version_the_deployment_no_longer_supports_stops_instead_of_retrying()
    {
        var agent = await StartAgentAsync(waitUntilRunning: false);

        // It would otherwise back off on the agent's clock, which never moves here, and never finish.
        await agent.Worker.ExecuteTask!.WaitAsync(Timeout, Ct);

        Assert.IsNull(agent.Identities.Load(), "it enrolled");
        Assert.AreEqual(1, agent.Network.Completed($"/{AgentProtocol.Routes.Enroll}"));
    }

    /// <summary>
    /// Starts an agent instance in a new root tenant (or on its existing enrollment when there's no token), returning
    /// once it has enrolled, connected, fetched its work and sent its first heartbeat, so the only things left to make it
    /// fetch again are the doorbell, a reconnect and the heartbeat timer.
    /// </summary>
    private async Task<RunningAgent> StartAgentAsync(string instanceId = "default", string? enrollmentToken = "issue",
        bool waitUntilRunning = true)
    {
        if (enrollmentToken == "issue")
        {
            var tenant = await _factory.CreateTenantAsync(_client, $"Root{instanceId}{Guid.NewGuid():N}");
            enrollmentToken = (await _factory.IssueEnrollmentTokenAsync(tenant.Id)).Secret;
        }

        var options = new AgentOptions
        {
            ApiBaseUrl = Signal9ApiFactory.HttpsLocalhost,
            EnrollmentToken = enrollmentToken,
            InstanceId = instanceId,
            StateDirectory = _stateDirectory,
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            TelemetryInterval = TimeSpan.FromHours(1),
        };
        var wrapped = Options.Create(options);
        var network = new InMemoryTransport(_factory.Server);
        var identities = new AgentIdentityStore(_keys, wrapped, _agentTime);
        var machine = new FakeMachine();
        var telemetry = new FakeTelemetry(_agentTime);
        var executor = new CommandExecutor([new CollectSystemInfoHandler(machine, telemetry)], _agentTime,
            NullLogger<CommandExecutor>.Instance);
        var worker = new AgentWorker(
            new AgentEnrollment(identities, network, machine, wrapped, NullLogger<AgentEnrollment>.Instance),
            new AgentCertificateRotation(identities, network, _agentTime, NullLogger<AgentCertificateRotation>.Instance),
            network,
            new AgentSession(executor, telemetry, machine, _agentTime, wrapped, NullLogger<AgentSession>.Instance),
            _agentTime,
            NullLogger<AgentWorker>.Instance);
        await worker.StartAsync(_stop.Token);
        var running = new RunningAgent(worker, network, options, identities);
        _agents.Add(running);
        if (!waitUntilRunning)
            return running;

        await WaitUntilAsync(() => Task.FromResult(identities.Load() is not null));
        var agentId = identities.Load()!.AgentId;
        await WaitUntilAsync(() => Task.FromResult(
            network.Hub?.State == HubConnectionState.Connected &&
            network.Completed($"/api/v1/agents/{agentId}/pending-commands") > 0 &&
            network.Completed($"/api/v1/agents/{agentId}/heartbeat") > 0));
        running.Agent = (await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{agentId}", Signal9Json.Options, Ct))!;
        return running;
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
            foreach (var agent in _agents.Where(a => a.Worker.ExecuteTask is { IsCompleted: true }))
                await agent.Worker.ExecuteTask!; // Surfaces why an agent stopped.
            await Task.Delay(20, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var agent in _agents)
            await agent.StopAsync();
        _stop.Dispose();
        _client.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_stateDirectory))
            Directory.Delete(_stateDirectory, recursive: true);
    }

    private sealed class RunningAgent(AgentWorker worker, InMemoryTransport network, AgentOptions options, AgentIdentityStore identities)
    {
        public AgentIdentityStore Identities { get; } = identities;

        public AgentWorker Worker { get; } = worker;

        public InMemoryTransport Network { get; } = network;

        public AgentOptions Options { get; } = options;

        public AgentDto Agent { get; set; } = null!;

        public Guid DeviceId => Agent.DeviceId!.Value;

        public async Task StopAsync()
        {
            try
            {
                await Worker.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            Worker.Dispose();
        }
    }

    /// <summary>Stands in for the TPM or DPAPI key store, shared by every instance on the "machine".</summary>
    private sealed class InMemoryKeyStore : IAgentKeyStore
    {
        private readonly Dictionary<string, ECParameters> _keys = [];

        public ECDsa Create(string name)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            lock (_keys)
                _keys[name] = key.ExportParameters(includePrivateParameters: true);
            return key;
        }

        public ECDsa? Open(string name)
        {
            lock (_keys)
                return _keys.TryGetValue(name, out var parameters) ? ECDsa.Create(parameters) : null;
        }

        public void Delete(string name)
        {
            lock (_keys)
                _keys.Remove(name);
        }
    }

    /// <summary>
    /// The agent's network, in memory: requests reach api through Container Apps' ingress, which forwards the agent's
    /// certificate, and the hub runs over a WebSocket the test can cut. It counts completed REST requests by path.
    /// </summary>
    private sealed class InMemoryTransport(TestServer server) : IAgentTransport
    {
        private readonly List<string> _completed = [];
        private readonly HashSet<string> _tokens = [];
        private volatile bool _offline;
        private WebSocket? _socket;
        private int _connections;

        public HubConnection? Hub { get; private set; }

        /// <summary>How many times the hub has connected.</summary>
        public int Connections => _connections;

        /// <summary>How many different access tokens the hub has connected with.</summary>
        public int DistinctTokens
        {
            get
            {
                lock (_tokens)
                    return _tokens.Count;
            }
        }

        public int Completed(string path)
        {
            lock (_completed)
                return _completed.Count(p => p == path);
        }

        public HttpClient CreateClient(AgentIdentity? identity, params DelegatingHandler[] handlers)
        {
            var ingress = server.CreateHandler(context => context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy);
            return new HttpClient(MutualTlsTransport.Chain([.. handlers, new Ingress(identity?.Certificate, this)], ingress))
            {
                BaseAddress = Signal9ApiFactory.HttpsLocalhost,
            };
        }

        public HubConnection ConnectToHub(AgentIdentity identity, Func<Task<string?>> accessToken,
            IReadOnlyDictionary<string, string> headers) => Hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, HubRoutes.Agents.TrimStart('/')), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    if (_offline)
                        throw new WebSocketException("The network is down.");
                    var token = await accessToken();
                    lock (_tokens)
                        _tokens.Add(token!);
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        request.HttpContext.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy;
                        request.Headers.Authorization = $"Bearer {token}";
                        request.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(identity.Certificate);
                        foreach (var (name, value) in headers)
                            request.Headers[name] = value;
                    };
                    _socket = await client.ConnectAsync(context.Uri, cancellationToken);
                    Interlocked.Increment(ref _connections);
                    return _socket;
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

        private sealed class Ingress(X509Certificate2? certificate, InMemoryTransport transport) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (certificate is not null)
                    request.Headers.Add(Signal9ApiFactory.ClientCertificateHeader, TestCertificates.ContainerAppsHeader(certificate));
                var response = await base.SendAsync(request, cancellationToken);
                lock (transport._completed)
                    transport._completed.Add(request.RequestUri!.AbsolutePath);
                return response;
            }
        }
    }

    /// <summary>A machine with fixed facts, so the test never reads the one it runs on.</summary>
    private sealed class FakeMachine : IMachineFactsSource
    {
        public MachineFacts GetFacts() => new("FAKE-PC", null, OsFamily.Linux, "Linux 6.18.0 #1 SMP", "6.18.0", "X64", "Fake CPU",
            8, 16384, "10.0.0.2", "00:11:22:33:44:55", AgentVersion.Current.ToString());

        public HardwareIdentity GetHardware() => HardwareIdentity.None;

        public DateTimeOffset? GetOsInstalledAt() => null;

        // Moved since enrollment, so a heartbeat's report shows.
        public string? GetPrimaryIpAddress() => "10.0.0.3";
    }

    private sealed class FakeTelemetry(TimeProvider time) : ITelemetrySource
    {
        public CreateTelemetrySampleRequest Collect() => new() { CollectedAt = time.GetUtcNow(), CpuPercent = 12.5 };
    }

    private sealed class RetryQuickly : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(Microsoft.AspNetCore.SignalR.Client.RetryContext retryContext) => TimeSpan.FromMilliseconds(50);
    }
}
