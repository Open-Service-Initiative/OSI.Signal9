using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Platforms;

/// <summary>
/// macOS: the agent has no readers here yet (sysctl, IOKit), so every reading is unknown. Its state lives in
/// <c>/Library/Application Support</c>, where Apple puts support files shared by every user.
/// </summary>
public sealed class MacOSPlatform : IPlatform
{
    public OsFamily Family => OsFamily.MacOS;

    public string StateDirectory => "/Library/Application Support/Signal9 Agent";

    public string? ReadDomain() => null;

    public string? ReadProcessorName() => null;

    public HardwareIdentity ReadHardware() => HardwareIdentity.None;

    public DateTimeOffset? ReadOsInstalledAt() => null;

    public CpuTimes? ReadCpuTimes() => null;

    public MemoryReading? ReadMemory() => null;
}
