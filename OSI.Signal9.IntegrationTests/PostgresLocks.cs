using Npgsql;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Who waits on whose locks, as PostgreSQL shows it: a test of serialization waits until it sees a request queued behind
/// a lock, rather than for a while. PostgreSQL announces no lock waits, so this one condition is polled, with no
/// deadline but <see cref="Signals.HangGuard"/>.
/// </summary>
internal static class PostgresLocks
{
    /// <summary>Completes once another session waits on a lock the session with process ID <paramref name="holder"/> holds.</summary>
    public static async Task BlockingAsync(string connectionString, int holder, CancellationToken cancellationToken)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(Signals.HangGuard);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(guard.Token);
        await using var blocked = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid)))", connection);
        blocked.Parameters.AddWithValue("holder", holder);
        try
        {
            while (!(bool)(await blocked.ExecuteScalarAsync(guard.Token))!)
                await Task.Delay(TimeSpan.FromMilliseconds(10), guard.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Assert.Fail("Nothing ever waited on the lock.");
        }
    }
}
