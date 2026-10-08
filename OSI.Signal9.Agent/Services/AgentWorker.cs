using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Enrolls the machine, then keeps a heartbeat, sends telemetry and runs commands pushed over SignalR.
/// Any failure tears the session down and starts over with exponential backoff.
/// </summary>
public sealed class AgentWorker(
    AgentApiClient api,
    SystemInfoProvider systemInfo,
    TelemetryCollector telemetry,
    CommandExecutor executor,
    IOptions<AgentOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private readonly AgentOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures), MaxBackoff.TotalSeconds));
                logger.LogError(ex, "Agent session failed; retrying in {Delay}", delay);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    /// <summary>One enrollment-to-disconnect session. Returns normally when the agent must re-enroll.</summary>
    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        var agent = await api.RegisterAsync(systemInfo.GetRegistration(), stoppingToken);
        logger.LogInformation("Enrolled as agent {AgentId} in tenant {TenantCode}", agent.Id, _options.TenantCode);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await using var hub = BuildHubConnection(agent.Id, session);
        await hub.StartAsync(session.Token);

        foreach (var command in await api.GetPendingCommandsAsync(agent.Id, session.Token))
            await RunCommandAsync(agent.Id, command, session.Token);

        var heartbeat = HeartbeatLoopAsync(agent.Id, session.Token);
        var telemetryLoop = TelemetryLoopAsync(agent.Id, session.Token);
        var finished = await Task.WhenAny(heartbeat, telemetryLoop);
        await session.CancelAsync();
        await finished; // Rethrows the failure, if any, so the outer loop backs off.
    }

    private HubConnection BuildHubConnection(Guid agentId, CancellationTokenSource session)
    {
        var hubUrl = new Uri(_options.ApiBaseUrl, $"{HubRoutes.Agents.TrimStart('/')}?agentId={agentId}");
        var hub = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .WithAutomaticReconnect()
            .Build();

        hub.On<AgentCommandDto>(nameof(IAgentHubClient.CommandQueued), command =>
            // Run off the SignalR receive loop so a slow command doesn't block other messages.
            _ = Task.Run(() => RunCommandAsync(agentId, command, session.Token), session.Token));
        hub.Reconnecting += error =>
        {
            logger.LogWarning(error, "Lost connection to the agent hub; reconnecting");
            return Task.CompletedTask;
        };
        // Automatic reconnect gave up: end the session so the worker re-enrolls and reconnects.
        hub.Closed += _ => session.CancelAsync();
        return hub;
    }

    private async Task RunCommandAsync(Guid agentId, AgentCommandDto command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await executor.ExecuteAsync(command, cancellationToken);
            await api.SendCommandResultAsync(agentId, command.Id, result, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not report the result of command {CommandId}", command.Id);
        }
    }

    private async Task HeartbeatLoopAsync(Guid agentId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        do
        {
            var (ipAddress, _) = SystemInfoProvider.GetPrimaryNetworkAddress();
            var known = await api.SendHeartbeatAsync(agentId,
                new HeartbeatRequest { IpAddress = ipAddress, AgentVersion = SystemInfoProvider.AgentVersion }, cancellationToken);
            if (!known)
            {
                logger.LogWarning("The API no longer knows agent {AgentId}; re-enrolling", agentId);
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task TelemetryLoopAsync(Guid agentId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TelemetryInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await api.SendTelemetryAsync(agentId, telemetry.Collect(), cancellationToken);
    }
}
