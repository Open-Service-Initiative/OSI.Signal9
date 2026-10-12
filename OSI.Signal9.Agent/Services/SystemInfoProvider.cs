using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Facts about this machine that the agent reports when it enrolls.
/// </summary>
public sealed class SystemInfoProvider(IOptions<AgentOptions> options)
{
    public static string AgentVersion { get; } =
        typeof(SystemInfoProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public RegisterAgentRequest GetRegistration()
    {
        var (ipAddress, macAddress) = GetPrimaryNetworkAddress();
        return new RegisterAgentRequest
        {
            TenantCode = options.Value.TenantCode,
            MachineName = Environment.MachineName,
            Domain = OperatingSystem.IsWindows() ? Environment.UserDomainName : null,
            OperatingSystem = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.Version.ToString(),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorName = GetProcessorName(),
            ProcessorCores = Environment.ProcessorCount,
            TotalMemoryMb = NativeMetrics.ReadMemory()?.TotalMb ?? 0,
            IpAddress = ipAddress,
            MacAddress = macAddress,
            AgentVersion = AgentVersion,
        };
    }

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
