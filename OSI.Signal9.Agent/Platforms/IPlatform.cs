using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Platforms;

/// <summary>
/// What the agent reads from the operating system it runs on, and where it keeps its state there. Program.cs picks the
/// implementation for the running OS; supporting another OS means adding one. A reading an OS can't give is null (or
/// <see cref="HardwareIdentity.None"/>), never a guess.
/// </summary>
public interface IPlatform
{
    OsFamily Family { get; }

    /// <summary>Where agent instances keep their state by default, each in a folder named after it.</summary>
    string StateDirectory { get; }

    /// <summary>The machine's domain, where the OS has one.</summary>
    string? ReadDomain();

    string? ReadProcessorName();

    /// <summary>The machine's SMBIOS system identifiers (#131).</summary>
    HardwareIdentity ReadHardware();

    /// <summary>When the operating system was installed, which tells api a machine was wiped or re-imaged.</summary>
    DateTimeOffset? ReadOsInstalledAt();

    /// <summary>Cumulative machine-wide CPU times.</summary>
    CpuTimes? ReadCpuTimes();

    MemoryReading? ReadMemory();
}

/// <summary>Cumulative idle and total CPU time, in arbitrary but consistent units.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Total);

/// <summary>Physical memory, in megabytes.</summary>
public readonly record struct MemoryReading(long TotalMb, long AvailableMb);
