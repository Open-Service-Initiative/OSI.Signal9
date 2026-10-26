using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OSI.Signal9.Server.Hosting;

/// <summary>
/// A background job that runs every <paramref name="interval"/>, counting from when the host starts, on the injected
/// <see cref="TimeProvider"/>, so tests drive it by advancing the clock. Each run gets a service scope of its own. A
/// failed run is logged, and the next one tries again; stopping the host cancels the run in progress and ends the job.
/// Each run is traced as a span named after the job (<see cref="BackgroundJobs"/>).
/// </summary>
public abstract class PeriodicJob(TimeSpan interval, IServiceScopeFactory scopes, TimeProvider time, ILogger logger)
    : BackgroundService
{
    /// <summary>One run, with services from its own scope.</summary>
    protected abstract Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);

    private PeriodicTimer? _timer;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Started with the host rather than in ExecuteAsync, which runs on the thread pool: the clock may move on
        // as soon as the host has started.
        _timer = new PeriodicTimer(interval, time);
        return base.StartAsync(cancellationToken);
    }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = _timer!;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var run = BackgroundJobs.StartRun(GetType().Name);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RunAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    run.Failed(exception);
                    logger.LogWarning(exception, "{Job} failed; the next run tries again.", GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
