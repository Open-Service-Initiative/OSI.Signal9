using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;

namespace OSI.Signal9.Agent.Services;

/// <summary>Builds an agent's connection to the agent hub, with automatic reconnects.</summary>
public delegate HubConnection AgentHubConnectionFactory(Guid agentId);

/// <summary>
/// One enrolled agent's session: it listens for the doorbell, sends heartbeats and telemetry, and runs its pending
/// commands. Work is fetched over REST when the doorbell rings, every time the hub (re)connects, and when a heartbeat
/// says work is waiting (ADR-0009); there's no other polling.
/// </summary>
public sealed class AgentSession(
    AgentApiClient api,
    CommandExecutor executor,
    TelemetryCollector telemetry,
    AgentHubConnectionFactory connectToHub,
    TimeProvider time,
    IOptions<AgentOptions> options,
    ILogger<AgentSession> logger)
{
    private readonly AgentOptions _options = options.Value;

    /// <summary>
    /// Runs until the agent must enroll again (it's unknown or was replaced), or throws when the session fails,
    /// including when the hub gives up reconnecting.
    /// </summary>
    public async Task RunAsync(AgentDto agent, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var work = new PendingWork(api, executor, agent.Id, logger);

        await using var hub = connectToHub(agent.Id);
        hub.On(nameof(IAgentHubClient.WorkAvailable), work.RequestFetch);
        hub.Reconnecting += error =>
        {
            logger.LogWarning(error, "Lost connection to the agent hub; reconnecting");
            return Task.CompletedTask;
        };
        // Doorbells rung while disconnected are lost, so fetch on every reconnect.
        hub.Reconnected += _ =>
        {
            work.RequestFetch();
            return Task.CompletedTask;
        };
        // Automatic reconnect gave up: end the session so the worker starts over.
        hub.Closed += _ => session.CancelAsync();

        await hub.StartAsync(session.Token);
        work.RequestFetch();

        var finished = await Task.WhenAny(
            work.RunAsync(session.Token),
            HeartbeatLoopAsync(agent.Id, work, session.Token),
            TelemetryLoopAsync(agent.Id, session.Token));
        await session.CancelAsync();
        await finished; // Rethrows the failure, if any, so the worker backs off.
    }

    private async Task HeartbeatLoopAsync(Guid agentId, PendingWork work, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, time);
        do
        {
            var (ipAddress, _) = SystemInfoProvider.GetPrimaryNetworkAddress();
            var response = await api.SendHeartbeatAsync(agentId,
                new HeartbeatRequest { IpAddress = ipAddress, AgentVersion = SystemInfoProvider.AgentVersion }, cancellationToken);
            if (response is null)
            {
                logger.LogWarning("Agent {AgentId} is unknown or was replaced; re-enrolling", agentId);
                return;
            }
            if (response.HasPendingWork)
                work.RequestFetch();
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task TelemetryLoopAsync(Guid agentId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TelemetryInterval, time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await api.SendTelemetryAsync(agentId, telemetry.Collect(), cancellationToken);
    }

    /// <summary>
    /// Fetches the agent's pending commands on request and runs each one. Requests made while a fetch is under way
    /// collapse into one more fetch, and a command still running when it's fetched again isn't started twice.
    /// </summary>
    private sealed class PendingWork(AgentApiClient api, CommandExecutor executor, Guid agentId, ILogger logger)
    {
        private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly ConcurrentDictionary<Guid, bool> _running = new();

        public void RequestFetch() => _requests.Writer.TryWrite(true);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(cancellationToken))
            {
                foreach (var command in await api.GetPendingCommandsAsync(agentId, cancellationToken))
                {
                    if (_running.TryAdd(command.Id, true))
                        _ = Task.Run(() => RunCommandAsync(command, cancellationToken), cancellationToken);
                }
            }
        }

        private async Task RunCommandAsync(CommandDto command, CancellationToken cancellationToken)
        {
            try
            {
                var result = await executor.ExecuteAsync(command, cancellationToken);
                await api.SendCommandResultAsync(command, result, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not report the result of command {CommandId}", command.Id);
            }
            finally
            {
                _running.TryRemove(command.Id, out _);
            }
        }
    }
}
