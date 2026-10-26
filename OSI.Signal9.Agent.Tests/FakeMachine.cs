using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Tests;

/// <summary>A machine with fixed facts, so tests never read the one they run on.</summary>
internal sealed class FakeMachine : IMachineFactsSource
{
    public MachineFacts Facts { get; init; } = new("FAKE-PC", null, OsFamily.Windows, "Microsoft Windows 10.0.26100", "10.0.26100",
        "X64", "Fake CPU", 8, 16384, "10.0.0.2", "00:11:22:33:44:55", "1.0.0");

    public HardwareIdentity Hardware { get; init; } = HardwareIdentity.None;

    public DateTimeOffset? OsInstalledAt { get; init; }

    public string? IpAddress { get; set; } = "10.0.0.2";

    public MachineFacts GetFacts() => Facts;

    public HardwareIdentity GetHardware() => Hardware;

    public DateTimeOffset? GetOsInstalledAt() => OsInstalledAt;

    public string? GetPrimaryIpAddress() => IpAddress;
}
