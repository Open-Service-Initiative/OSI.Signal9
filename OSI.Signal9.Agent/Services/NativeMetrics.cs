using System.Runtime.InteropServices;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// Machine-wide CPU and memory readings. Windows uses Win32 APIs; Linux reads /proc so the agent can be
/// developed under WSL. Other platforms report nothing rather than guessing.
/// </summary>
internal static partial class NativeMetrics
{
    /// <summary>Cumulative idle and total CPU time, in arbitrary but consistent units.</summary>
    public static (ulong Idle, ulong Total)? ReadCpuTimes()
    {
        if (OperatingSystem.IsWindows())
        {
            // Kernel time includes idle time.
            return GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : null;
        }

        if (OperatingSystem.IsLinux())
        {
            var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(ulong.Parse).ToArray();
            var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0); // idle + iowait
            return (idle, fields.Aggregate(0UL, (sum, value) => sum + value));
        }

        return null;
    }

    public static (long TotalMb, long AvailableMb)? ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? ((long)(status.TotalPhys / 1024 / 1024), (long)(status.AvailPhys / 1024 / 1024))
                : null;
        }

        if (OperatingSystem.IsLinux())
        {
            var values = File.ReadLines("/proc/meminfo")
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim().Split(' ')[0]));
            return (values["MemTotal"] / 1024, values["MemAvailable"] / 1024);
        }

        return null;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
