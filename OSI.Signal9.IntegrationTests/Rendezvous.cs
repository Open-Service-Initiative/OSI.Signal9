using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Holds the first <paramref name="parties"/> commands whose SQL contains <paramref name="marker"/> until all of them
/// have arrived, or until <paramref name="patience"/> runs out for those that came first: concurrent requests then
/// reach that query together, unless something serializes them before it. Add one to the API's DbContext options.
/// </summary>
internal sealed class Rendezvous(string marker, int parties, TimeSpan patience) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _everyone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains(marker, StringComparison.Ordinal))
        {
            var arrival = Interlocked.Increment(ref _arrived);
            if (arrival == parties)
                _everyone.TrySetResult();
            if (arrival <= parties)
                await Task.WhenAny(_everyone.Task, Task.Delay(patience, cancellationToken));
        }
        return result;
    }
}
