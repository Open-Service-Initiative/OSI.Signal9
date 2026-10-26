using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OSI.Signal9.Agent.Identity;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// One enrolled agent's session: it listens for the doorbell, sends heartbeats and telemetry, and runs its pending
/// commands. Work is fetched over REST when the doorbell rings, every time the hub (re)connects, and when a heartbeat
/// says work is waiting (ADR-0009); there's no other polling. The hub closes when the agent's token expires, and
/// reconnects with a fresh one. The session ends when the agent's certificate falls due for rotation.
/// </summary>
public sealed class AgentSession(
    CommandExecutor executor,
    TelemetryCollector telemetry,
    TimeProvider time,
    IOptions<AgentOptions> options,
    ILogger<AgentSession> logger)
{
    /// <summary>How often the session checks whether the certificate has fallen due for rotation.</summary>
    public static readonly TimeSpan RenewalCheckInterval = TimeSpan.FromHours(1);

    private readonly AgentOptions _options = options.Value;

    /// <summary>
    /// Runs until stopped or <paramref name="renewAt"/>, when the certificate is due for rotation, or throws when the
    /// session fails: when the API refuses the agent, or the hub gives up reconnecting.
    /// </summary>
    public async Task RunAsync(AgentConnection connection, DateTimeOffset renewAt, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var api = connection.Api;
        var work = new PendingWork(api, executor, connection.AgentId, logger);

        await using var hub = connection.ConnectToHub();
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
        var closed = new TaskCompletionSource<Exception?>();
        hub.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        await hub.StartAsync(session.Token);
        work.RequestFetch();

        var finished = await Task.WhenAny(
            closed.Task,
            work.RunAsync(session.Token),
            HeartbeatLoopAsync(api, connection.AgentId, work, session.Token),
            TelemetryLoopAsync(api, connection.AgentId, session.Token),
            UntilAsync(renewAt, session.Token));
        await session.CancelAsync();
        if (finished == closed.Task && !stoppingToken.IsCancellationRequested)
            throw new InvalidOperationException("The agent hub connection closed.", closed.Task.Result);
        await finished; // Rethrows the failure, if any, so the worker backs off.
    }

    private async Task HeartbeatLoopAsync(AgentApiClient api, Guid agentId, PendingWork work, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, time);
        do
        {
            var (ipAddress, _) = SystemInfoProvider.GetPrimaryNetworkAddress();
            var response = await api.SendHeartbeatAsync(agentId,
                new HeartbeatRequest { IpAddress = ipAddress }, cancellationToken);
            if (response.HasPendingWork)
                work.RequestFetch();
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    // Checked on an interval rather than with one long delay, which timers cap at about 49 days.
    private async Task UntilAsync(DateTimeOffset renewAt, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RenewalCheckInterval, time);
        while (time.GetUtcNow() < renewAt && await timer.WaitForNextTickAsync(cancellationToken))
        {
        }
    }

    private async Task TelemetryLoopAsync(AgentApiClient api, Guid agentId, CancellationToken cancellationToken)
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
