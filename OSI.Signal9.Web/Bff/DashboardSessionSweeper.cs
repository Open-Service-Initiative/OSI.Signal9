namespace OSI.Signal9.Web.Bff;

/// <summary>
/// Ends sessions whose person never came back once they pass their idle or absolute cap, revoking their refresh
/// tokens at auth (story 48), and retries revocations auth didn't confirm. Every replica sweeps; whichever ends a
/// session, or claims its retry, revokes it.
/// </summary>
public sealed class DashboardSessionSweeper(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<DashboardSessionSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sessions = scope.ServiceProvider.GetRequiredService<DashboardSessions>();
                await sessions.EndExpiredAsync(stoppingToken);
                await sessions.RetryRevocationsAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Sweeping dashboard sessions failed; the next sweep tries again.");
            }
        }
    }
}
