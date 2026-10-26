using System.Diagnostics;
using OSI.Signal9.Server.Hosting;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Watches the spans every host exports for its background jobs' runs (<see cref="BackgroundJobs"/>), as an operator's
/// tracing would, so a test can wait for the run a clock advance starts to finish before it checks the outcome.
/// </summary>
public sealed class BackgroundJobRuns : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Waiter> _waiting = [];
    private readonly Dictionary<Activity, List<Waiter>> _running = [];

    private sealed record Waiter(string Job, TaskCompletionSource Finished);

    public BackgroundJobRuns()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackgroundJobs.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = Started,
            ActivityStopped = Stopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// Completes when the next run of <paramref name="job"/> to start from now on has finished, or fails with why the
    /// run failed. Ask before the clock advance that starts the run.
    /// </summary>
    public Task NextRunAsync(string job)
    {
        var waiter = new Waiter(job, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_waiting)
            _waiting.Add(waiter);
        return waiter.Finished.Task;
    }

    private void Started(Activity run)
    {
        lock (_waiting)
        {
            var waiters = _waiting.Where(w => w.Job == run.OperationName).ToList();
            if (waiters.Count == 0)
                return;
            _waiting.RemoveAll(waiters.Contains);
            _running[run] = waiters;
        }
    }

    private void Stopped(Activity run)
    {
        List<Waiter>? waiters;
        lock (_waiting)
            if (!_running.Remove(run, out waiters))
                return;
        foreach (var waiter in waiters)
        {
            if (run.Status == ActivityStatusCode.Error)
                waiter.Finished.TrySetException(new InvalidOperationException($"{run.OperationName} failed: {run.StatusDescription}"));
            else
                waiter.Finished.TrySetResult();
        }
    }

    public void Dispose() => _listener.Dispose();
}
