using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OSI.Signal9.Agent.Platforms;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Services;

/// <summary>Where the agent's facts about its machine come from: the real machine, or a fake in tests.</summary>
public interface IMachineFactsSource
{
    /// <summary>What this machine is: the facts the agent reports when it enrolls, and in system info.</summary>
    MachineFacts GetFacts();

    /// <summary>The machine's SMBIOS system identifiers, which identify a reinstalled agent's device (#131).</summary>
    HardwareIdentity GetHardware();

    /// <summary>When the operating system was installed, if the agent can tell.</summary>
    DateTimeOffset? GetOsInstalledAt();

    /// <summary>The IPv4 address of the interface with a default gateway, which heartbeats report.</summary>
    string? GetPrimaryIpAddress();
}

/// <summary>The real machine's facts: what .NET reports on every OS, and the rest from its <see cref="IPlatform"/>.</summary>
public sealed class SystemInfoProvider(IPlatform platform) : IMachineFactsSource
{
    public MachineFacts GetFacts()
    {
        var (ipAddress, macAddress) = GetPrimaryNetworkAddress();
        return new MachineFacts(
            MachineName: Environment.MachineName,
            Domain: platform.ReadDomain(),
            OsFamily: platform.Family,
            OperatingSystem: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.Version.ToString(),
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessorName: platform.ReadProcessorName(),
            ProcessorCores: Environment.ProcessorCount,
            TotalMemoryMb: platform.ReadMemory()?.TotalMb ?? 0,
            IpAddress: ipAddress,
            MacAddress: macAddress,
            AgentVersion: AgentVersion.Current.ToString());
    }

    public HardwareIdentity GetHardware() => platform.ReadHardware();

    public DateTimeOffset? GetOsInstalledAt() => platform.ReadOsInstalledAt();

    public string? GetPrimaryIpAddress() => GetPrimaryNetworkAddress().IpAddress;

    private static (string? IpAddress, string? MacAddress) GetPrimaryNetworkAddress()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (Nic: n, Address: n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address))
            .FirstOrDefault(n => n.Address is not null && n.Nic.GetIPProperties().GatewayAddresses.Count > 0);

        if (nic.Nic is null)
            return (null, null);

        var mac = nic.Nic.GetPhysicalAddress().GetAddressBytes();
        return (nic.Address!.ToString(), mac.Length == 6 ? string.Join(':', mac.Select(b => b.ToString("X2"))) : null);
    }
}

/// <summary>Facts about the machine an agent runs on.</summary>
/// <param name="OperatingSystem">The OS's own description, uncut: enrollment cuts it to api's limit.</param>
public sealed record MachineFacts(
    string MachineName,
    string? Domain,
    OsFamily OsFamily,
    string OperatingSystem,
    string? OsVersion,
    string? Architecture,
    string? ProcessorName,
    int ProcessorCores,
    long TotalMemoryMb,
    string? IpAddress,
    string? MacAddress,
    string AgentVersion);
