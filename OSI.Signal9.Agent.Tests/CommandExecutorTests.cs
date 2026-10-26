using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Agent.Tests;

[TestClass]
public sealed class CommandExecutorTests(TestContext testContext)
{
    private readonly FakeTimeProvider _time = new();

    private CancellationToken Ct => testContext.CancellationToken;

    [TestMethod]
    public async Task Collecting_system_info_succeeds_with_the_machine_s_facts_and_a_telemetry_sample_as_output()
    {
        var executor = Executor(new CollectSystemInfoHandler(new FakeMachine(), new FakeTelemetry(_time)));

        var result = await executor.ExecuteAsync(Command(CommandType.CollectSystemInfo), Ct);

        Assert.IsTrue(result.Succeeded);
        using var output = JsonDocument.Parse(result.Output!);
        Assert.AreEqual("FAKE-PC", output.RootElement.GetProperty("system").GetProperty("machineName").GetString());
        Assert.AreEqual(12.5, output.RootElement.GetProperty("telemetry").GetProperty("cpuPercent").GetDouble());
    }

    [TestMethod]
    public async Task An_unsupported_command_fails_explicitly()
    {
        var result = await Executor().ExecuteAsync(Command(CommandType.RunScript), Ct);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("RunScript is not supported by this agent version.", result.Error);
    }

    [TestMethod]
    public async Task A_command_still_running_at_its_timeout_fails_as_timed_out()
    {
        var handler = new NeverFinishingHandler(CommandType.RunScript);
        var running = Executor(handler).ExecuteAsync(Command(CommandType.RunScript, timeoutSeconds: 60), Ct);
        var token = await handler.Started.WaitAsync(Ct);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.IsFalse(token.IsCancellationRequested, "cancelled before the timeout");
        _time.Advance(TimeSpan.FromSeconds(1));
        var result = await running.WaitAsync(Ct);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Timed out after 60 seconds.", result.Error);
    }

    private CommandExecutor Executor(params ICommandHandler[] handlers) =>
        new(handlers, _time, NullLogger<CommandExecutor>.Instance);

    private static CommandDto Command(CommandType type, int timeoutSeconds = 60) => new(
        Guid.NewGuid(), Guid.NewGuid(), "https://api.invalid/api/v1/devices/x/commands/y", type, null,
        timeoutSeconds, CommandStatus.Pending, DateTimeOffset.UnixEpoch, null, null, null);

    private sealed class FakeTelemetry(TimeProvider time) : ITelemetrySource
    {
        public CreateTelemetrySampleRequest Collect() => new() { CollectedAt = time.GetUtcNow(), CpuPercent = 12.5 };
    }

    /// <summary>Runs until it's cancelled, like a long script.</summary>
    private sealed class NeverFinishingHandler(CommandType type) : ICommandHandler
    {
        private readonly TaskCompletionSource<CancellationToken> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CancellationToken> Started => _started.Task;

        public CommandType Type => type;

        public async Task<CommandResultRequest> HandleAsync(CommandDto command, CancellationToken cancellationToken)
        {
            _started.SetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
