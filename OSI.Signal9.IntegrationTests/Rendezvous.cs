using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Holds the first command whose SQL contains <paramref name="marker"/> from each of <paramref name="parties"/>
/// database sessions until all of them have arrived: concurrent requests then reach that query together. If something
/// serializes them before it, each waits instead until PostgreSQL shows another session queued behind a lock its own
/// session holds. Add one instance to the API's DbContext options, shared by every request; <paramref name="connectionString"/>
/// is the database's, to watch its locks from.
/// </summary>
internal sealed class Rendezvous(string connectionString, string marker, int parties) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _everyone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> _arrived = [];

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!command.CommandText.Contains(marker, StringComparison.Ordinal))
            return result;
        var session = ((NpgsqlConnection)command.Connection!).ProcessID;
        lock (_arrived)
        {
            if (_arrived.Count >= parties || !_arrived.Add(session))
                return result;
            if (_arrived.Count == parties)
                _everyone.TrySetResult();
        }

        using var arrived = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var blocking = PostgresLocks.BlockingAsync(connectionString, session, arrived.Token);
        await Task.WhenAny(_everyone.Task, blocking);
        await arrived.CancelAsync();
        if (blocking.IsFaulted)
            await blocking;
        return result;
    }
}
