using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Facts about this machine that the agent reports when it enrolls.
/// </summary>
/// <param name="readHardware">Reads the machine's hardware identifiers; <see cref="HardwareIdentity.Read"/> by default.</param>
public sealed class SystemInfoProvider(Func<HardwareIdentity>? readHardware = null)
{
    private readonly Func<HardwareIdentity> _readHardware = readHardware ?? HardwareIdentity.Read;

    public static string AgentVersion { get; } =
        typeof(SystemInfoProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>What this machine is: the facts the agent reports when it enrolls.</summary>
    public MachineFacts GetFacts()
    {
        var (ipAddress, macAddress) = GetPrimaryNetworkAddress();
        return new MachineFacts(
            MachineName: Environment.MachineName,
            Domain: OperatingSystem.IsWindows() ? Environment.UserDomainName : null,
            OperatingSystem: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.Version.ToString(),
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessorName: GetProcessorName(),
            ProcessorCores: Environment.ProcessorCount,
            TotalMemoryMb: NativeMetrics.ReadMemory()?.TotalMb ?? 0,
            IpAddress: ipAddress,
            MacAddress: macAddress,
            AgentVersion: AgentVersion);
    }

    /// <summary>An enrollment request presenting <paramref name="enrollmentToken"/> and the agent's certificate.</summary>
    public EnrollAgentRequest GetEnrollment(string enrollmentToken, string certificatePem)
    {
        var facts = GetFacts();
        var hardware = _readHardware();
        return new EnrollAgentRequest
        {
            EnrollmentToken = enrollmentToken,
            Certificate = certificatePem,
            MachineName = facts.MachineName,
            Domain = facts.Domain,
            OperatingSystem = facts.OperatingSystem,
            OsVersion = facts.OsVersion,
            Architecture = facts.Architecture,
            ProcessorName = facts.ProcessorName,
            ProcessorCores = facts.ProcessorCores,
            TotalMemoryMb = facts.TotalMemoryMb,
            IpAddress = facts.IpAddress,
            MacAddress = facts.MacAddress,
            AgentVersion = facts.AgentVersion,
            SmbiosUuid = hardware.SmbiosUuid,
            Manufacturer = Truncate(hardware.Manufacturer),
            SerialNumber = Truncate(hardware.SerialNumber),
        };
    }

    // The API takes at most 255 characters; firmware strings are never meaningfully longer.
    private static string? Truncate(string? value) => value is { Length: > 255 } ? value[..255] : value;

    public static (string? IpAddress, string? MacAddress) GetPrimaryNetworkAddress()
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

    private static string? GetProcessorName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim();
            }
            if (OperatingSystem.IsLinux())
            {
                return File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?
                    .Split(':', 2)[1].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not worth failing enrollment over.
        }
        return null;
    }
}

/// <summary>Facts about the machine an agent runs on.</summary>
public sealed record MachineFacts(
    string MachineName,
    string? Domain,
    string OperatingSystem,
    string? OsVersion,
    string? Architecture,
    string? ProcessorName,
    int ProcessorCores,
    long TotalMemoryMb,
    string? IpAddress,
    string? MacAddress,
    string AgentVersion);
