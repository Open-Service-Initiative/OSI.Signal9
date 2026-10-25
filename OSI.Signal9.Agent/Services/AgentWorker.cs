using OSI.Signal9.Agent.Identity;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Enrolls this agent instance if it hasn't yet, then runs an <see cref="AgentSession"/> as it until it ends. Any
/// failure starts over with exponential backoff.
/// </summary>
public sealed class AgentWorker(
    AgentEnrollment enrollment,
    IAgentTransport transport,
    AgentSession session,
    TimeProvider time,
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
                var identity = await enrollment.EnsureEnrolledAsync(stoppingToken);
                using var connection = new AgentConnection(identity, transport, time);
                await session.RunAsync(connection, stoppingToken);
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
                await Task.Delay(delay, time, stoppingToken);
            }
        }
    }
}
