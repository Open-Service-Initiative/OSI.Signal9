using OSI.Signal9.Server.Hosting;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// Ends sessions whose person never came back once they pass their idle or absolute cap, revoking their refresh
/// tokens at auth (story 48), and retries revocations auth didn't confirm. Every replica sweeps; whichever ends a
/// session, or claims its retry, revokes it.
/// </summary>
public sealed class DashboardSessionSweeper(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<DashboardSessionSweeper> logger) : PeriodicJob(Interval, scopes, time, logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var sessions = services.GetRequiredService<DashboardSessions>();
        await sessions.EndExpiredAsync(cancellationToken);
        await sessions.RetryRevocationsAsync(cancellationToken);
    }
}
