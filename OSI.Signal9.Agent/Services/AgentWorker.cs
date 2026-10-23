namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Enrolls the machine, then runs an <see cref="AgentSession"/> until it ends. Any failure starts over with
/// exponential backoff.
/// </summary>
public sealed class AgentWorker(
    AgentApiClient api,
    SystemInfoProvider systemInfo,
    AgentSession session,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var agent = await api.RegisterAsync(systemInfo.GetRegistration(), stoppingToken);
                logger.LogInformation("Enrolled as agent {AgentId} of device {DeviceId} in tenant {TenantId}", agent.Id, agent.DeviceId, agent.TenantId);
                await session.RunAsync(agent, stoppingToken);
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
}
