using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Platforms;

/// <summary>Windows: Win32 and the registry. State lives in <c>%ProgramData%\Signal9\Agent</c>.</summary>
public sealed class WindowsPlatform : IPlatform
{
    private const uint RawSmbiosProvider = 0x52534D42; // 'RSMB'

    public OsFamily Family => OsFamily.Windows;

    public string StateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Signal9", "Agent");

    public string? ReadDomain() => Environment.UserDomainName;

    [SupportedOSPlatform("windows")]
    public string? ReadProcessorName() =>
        (ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") as string)?.Trim();

    /// <summary>The firmware's raw SMBIOS table.</summary>
    public HardwareIdentity ReadHardware()
    {
        var size = GetSystemFirmwareTable(RawSmbiosProvider, 0, null, 0);
        if (size == 0)
            return HardwareIdentity.None;
        var buffer = new byte[size];
        return GetSystemFirmwareTable(RawSmbiosProvider, 0, buffer, size) == size
            ? SmbiosTable.ReadSystemInformation(buffer)
            : HardwareIdentity.None;
    }

    /// <summary>The registry's <c>InstallDate</c>, which <c>Win32_OperatingSystem.InstallDate</c> reports too.</summary>
    [SupportedOSPlatform("windows")]
    public DateTimeOffset? ReadOsInstalledAt() =>
        FromInstallDate(ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate"));

    /// <summary>Windows' <c>InstallDate</c> value: seconds since the Unix epoch, as a DWORD. Zero or missing is unknown.</summary>
    public static DateTimeOffset? FromInstallDate(object? value) =>
        value is int seconds && seconds != 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds) : null;

    /// <summary>Kernel time includes idle time.</summary>
    public CpuTimes? ReadCpuTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel + user) : null;

    public MemoryReading? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? new MemoryReading((long)(status.TotalPhys / 1024 / 1024), (long)(status.AvailPhys / 1024 / 1024))
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static object? ReadRegistry(string key, string name)
    {
        try
        {
            using var subKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            return subKey?.GetValue(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId,
        [Out] byte[]? firmwareTableBuffer, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
