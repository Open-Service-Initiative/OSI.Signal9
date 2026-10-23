using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Commands;

namespace OSI.Signal9.Agent.Tests;

[TestClass]
public sealed class CommandExecutorTests(TestContext testContext)
{
    private static readonly Guid TenantId = Guid.Parse("4f0c2b8e-7a51-4d3e-9b6a-1c2d3e4f5a6b");

    private readonly CommandExecutor _executor = new(
        new SystemInfoProvider(Options.Create(new AgentOptions { ApiBaseUrl = new Uri("https://api.invalid"), TenantId = TenantId })),
        new TelemetryCollector(new FakeTimeProvider(), NullLogger<TelemetryCollector>.Instance),
        NullLogger<CommandExecutor>.Instance);

    private CancellationToken Ct => testContext.CancellationToken;

    [TestMethod]
    public async Task Collecting_system_info_succeeds_with_this_machine_s_registration_as_output()
    {
        var result = await _executor.ExecuteAsync(Command(CommandType.CollectSystemInfo), Ct);

        Assert.IsTrue(result.Succeeded);
        using var output = JsonDocument.Parse(result.Output!);
        var registration = output.RootElement.GetProperty("registration");
        Assert.AreEqual(TenantId, registration.GetProperty("tenantId").GetGuid());
        Assert.AreEqual(Environment.MachineName, registration.GetProperty("machineName").GetString());
    }

    [TestMethod]
    [DataRow(CommandType.RestartService)]
    [DataRow(CommandType.RunScript)]
    [DataRow(CommandType.RestartMachine)]
    public async Task An_unsupported_command_fails_explicitly(CommandType type)
    {
        var result = await _executor.ExecuteAsync(Command(type), Ct);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual($"{type} is not supported by this agent version.", result.Error);
    }

    private static CommandDto Command(CommandType type) => new(
        Guid.NewGuid(), Guid.NewGuid(), "https://api.invalid/api/v1/devices/x/commands/y", type, null,
        CommandPriority.Normal, 60, CommandStatus.Pending, DateTimeOffset.UnixEpoch, null, null, null);
}
