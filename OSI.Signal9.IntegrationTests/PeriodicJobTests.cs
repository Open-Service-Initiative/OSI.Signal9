using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Server.Hosting;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Every background job runs on the injected clock, so a test drives it by advancing a <see cref="FakeTimeProvider"/>.
/// </summary>
[TestClass]
public sealed class PeriodicJobTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Each_interval_on_the_clock_runs_the_job_in_a_scope_of_its_own()
    {
        var time = new FakeTimeProvider();
        var runs = Channel.CreateUnbounded<Run>();
        using var host = await StartAsync(time, runs, _ => Task.CompletedTask);

        Assert.IsFalse(runs.Reader.TryRead(out _), "The job ran before its interval passed.");
        time.Advance(Probe.Every);
        var first = await NextAsync(runs);
        time.Advance(Probe.Every);
        var second = await NextAsync(runs);

        Assert.AreNotSame(first.Scope, second.Scope);
    }

    [TestMethod]
    public async Task A_failed_run_doesnt_stop_the_next()
    {
        var time = new FakeTimeProvider();
        var runs = Channel.CreateUnbounded<Run>();
        var failures = 0;
        using var host = await StartAsync(time, runs, _ => Interlocked.Increment(ref failures) == 1
            ? throw new InvalidOperationException("The database went away.")
            : Task.CompletedTask);

        time.Advance(Probe.Every);
        await NextAsync(runs);
        time.Advance(Probe.Every);
        await NextAsync(runs);

        Assert.AreEqual(2, failures);
    }

    [TestMethod]
    public async Task Stopping_the_host_cancels_the_run_in_progress()
    {
        var time = new FakeTimeProvider();
        var runs = Channel.CreateUnbounded<Run>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await StartAsync(time, runs, async cancellationToken =>
        {
            await using var _ = cancellationToken.Register(cancelled.SetResult);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });
        time.Advance(Probe.Every);
        await NextAsync(runs);

        await host.StopAsync(TestContext.CancellationToken).WaitAsync(Patience, TestContext.CancellationToken);

        Assert.IsTrue(cancelled.Task.IsCompleted);
    }

    private async Task<IHost> StartAsync(FakeTimeProvider time, Channel<Run> runs, Func<CancellationToken, Task> body)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(runs);
        builder.Services.AddSingleton(body);
        builder.Services.AddScoped<RunScope>();
        builder.Services.AddHostedService<Probe>();
        var host = builder.Build();
        await host.StartAsync(TestContext.CancellationToken);
        return host;
    }

    private async Task<Run> NextAsync(Channel<Run> runs) =>
        await runs.Reader.ReadAsync(TestContext.CancellationToken).AsTask().WaitAsync(Patience, TestContext.CancellationToken);

    private sealed record Run(RunScope Scope);

    private sealed class RunScope;

    private sealed class Probe(IServiceScopeFactory scopes, TimeProvider time, ILogger<Probe> logger)
        : PeriodicJob(Every, scopes, time, logger)
    {
        public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

        protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            await services.GetRequiredService<Channel<Run>>().Writer.WriteAsync(new Run(services.GetRequiredService<RunScope>()), cancellationToken);
            await services.GetRequiredService<Func<CancellationToken, Task>>()(cancellationToken);
        }
    }
}
