using OSI.Signal9.Agent.Identity;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Enrolls this agent instance if it hasn't yet, rotates its key and certificate when they're due, then runs an
/// <see cref="AgentSession"/> as it until it ends: when the certificate next falls due for rotation, or on failure. Any
/// failure starts over with exponential backoff, except api refusing the agent's version, which stops it until it's
/// updated.
/// </summary>
public sealed class AgentWorker(
    AgentEnrollment enrollment,
    AgentCertificateRotation rotation,
    IAgentTransport transport,
    AgentSession session,
    TimeProvider time,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>How long after a failed rotation the agent tries again, carrying on with its current certificate meanwhile.</summary>
    public static readonly TimeSpan RotationRetry = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await enrollment.EnsureEnrolledAsync(stoppingToken);
                DateTimeOffset renewAt;
                try
                {
                    identity = await rotation.RotateIfDueAsync(identity, stoppingToken);
                    renewAt = AgentIdentityStore.RenewalDue(identity.Certificate);
                }
                catch (HttpRequestException ex)
                {
                    renewAt = time.GetUtcNow() + RotationRetry;
                    logger.LogWarning(ex, "Agent {AgentId} couldn't rotate its certificate; retrying at {RetryAt}", identity.AgentId, renewAt);
                }

                using var connection = new AgentConnection(identity, transport, time);
                await session.RunAsync(connection, renewAt, stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (UnsupportedAgentVersionException ex)
            {
                // Retrying can't help: only an update can. The agent stays installed but sends nothing more.
                logger.LogCritical(ex,
                    "The deployment no longer supports agent version {AgentVersion} (it supports {MinimumVersion} to {MaximumVersion}); the agent has stopped and must be updated",
                    AgentVersion.Current, ex.MinimumVersion ?? "unknown", ex.MaximumVersion ?? "unknown");
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
