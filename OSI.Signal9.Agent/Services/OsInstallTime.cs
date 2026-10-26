using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// When this machine's operating system was installed, which the agent reports at enrollment: an installation from the
/// last day tells the API the machine was probably wiped or re-imaged. On Windows it's the registry's
/// <c>InstallDate</c>, which <c>Win32_OperatingSystem.InstallDate</c> reports too; on Linux, the root filesystem's
/// birth time, where the kernel and filesystem record one. Unknown otherwise.
/// </summary>
public static class OsInstallTime
{
    public static DateTimeOffset? Read() =>
        OperatingSystem.IsWindows() ? ReadWindows()
        : OperatingSystem.IsLinux() ? ReadLinux()
        : null;

    /// <summary>Windows' <c>InstallDate</c> value: seconds since the Unix epoch, as a DWORD. Zero or missing is unknown.</summary>
    public static DateTimeOffset? FromInstallDate(object? value) =>
        value is int seconds && seconds != 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds) : null;

    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? ReadWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return FromInstallDate(key?.GetValue("InstallDate"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private const int AtFdCwd = -100;
    private const uint StatxBirthTime = 0x800;
    // struct statx (linux/stat.h): stx_mask is the first field, and stx_btime.tv_sec sits at offset 0x50.
    private const int StatxSize = 256;
    private const int BirthTimeSecondsOffset = 0x50;

    private static DateTimeOffset? ReadLinux()
    {
        try
        {
            var buffer = new byte[StatxSize];
            if (statx(AtFdCwd, "/", 0, StatxBirthTime, buffer) != 0
                || (BinaryPrimitives.ReadUInt32LittleEndian(buffer) & StatxBirthTime) == 0)
                return null;
            var seconds = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(BirthTimeSecondsOffset));
            return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A C library without statx (glibc before 2.28): the birth time can't be read.
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags, uint mask, byte[] buffer);
}
