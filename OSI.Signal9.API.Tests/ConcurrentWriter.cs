using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// Plays a concurrent writer: just before a DbContext's first UPDATE or DELETE, commits <paramref name="sql"/> on its
/// own connection, so the API's write races a change made after it read the row. Add one per DbContext (per request).
/// </summary>
internal sealed class ConcurrentWriter(string connectionString, string sql) : DbCommandInterceptor
{
    private int _raced;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await RaceAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await RaceAsync(command, cancellationToken);
        return result;
    }

    private async Task RaceAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var text = command.CommandText.TrimStart();
        if (!text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            return;
        if (Interlocked.Exchange(ref _raced, 1) == 1)
            return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var concurrent = new NpgsqlCommand(sql, connection);
        await concurrent.ExecuteNonQueryAsync(cancellationToken);
    }
}
