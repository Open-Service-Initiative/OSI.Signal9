using System.Buffers.Binary;
using System.Runtime.InteropServices;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Platforms;

/// <summary>
/// Linux: readings from <c>/proc</c> and sysfs, and the root filesystem's birth time. State lives in
/// <c>/var/lib/signal9-agent</c> (FHS 3.0 §5.8).
/// </summary>
/// <param name="root">The filesystem root the readings are taken under; another directory in tests.</param>
public sealed class LinuxPlatform(string root = "/") : IPlatform
{
    public OsFamily Family => OsFamily.Linux;

    public string StateDirectory => "/var/lib/signal9-agent";

    private string PathOf(string path) => Path.Combine(root, path.TrimStart('/'));

    public string? ReadDomain() => null;

    public string? ReadProcessorName() =>
        Lines("/proc/cpuinfo")?
            .FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?
            .Split(':', 2) is [_, var name] ? name.Trim() : null;

    /// <summary>The kernel's DMI attributes (root-only for the serial).</summary>
    public HardwareIdentity ReadHardware() => new(
        Guid.TryParse(Text("/sys/class/dmi/id/product_uuid"), out var uuid) ? uuid : null,
        Text("/sys/class/dmi/id/sys_vendor"),
        Text("/sys/class/dmi/id/product_serial"));

    /// <summary>Idle is idle plus iowait; the total is user to steal, since guest time is already counted in user (proc(5)).</summary>
    public CpuTimes? ReadCpuTimes()
    {
        if (Lines("/proc/stat")?.FirstOrDefault() is not { } line || !line.StartsWith("cpu ", StringComparison.Ordinal))
            return null;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(ulong.Parse).ToArray();
        return fields.Length < 4 ? null : new CpuTimes(fields[3] + (fields.Length > 4 ? fields[4] : 0), fields.Aggregate(0UL, (sum, value) => sum + value));
    }

    public MemoryReading? ReadMemory()
    {
        var values = Lines("/proc/meminfo")?
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim().Split(' ')[0]));
        return values is not null && values.TryGetValue("MemTotal", out var total) && values.TryGetValue("MemAvailable", out var available)
            ? new MemoryReading(total / 1024, available / 1024)
            : null;
    }

    public DateTimeOffset? ReadOsInstalledAt()
    {
        try
        {
            var buffer = new byte[StatxSize];
            return statx(AtFdCwd, root, 0, StatxBirthTime, buffer) == 0 ? BirthTime(buffer) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A C library without statx (glibc before 2.28): the birth time can't be read.
            return null;
        }
    }

    /// <summary>
    /// The birth time in a <c>struct statx</c> (linux/stat.h), if the kernel and filesystem recorded one: it sets
    /// <c>STATX_BTIME</c> in <c>stx_mask</c>, the first field, and <c>stx_btime.tv_sec</c> sits at offset 0x50.
    /// </summary>
    public static DateTimeOffset? BirthTime(ReadOnlySpan<byte> statxBuffer)
    {
        if ((BinaryPrimitives.ReadUInt32LittleEndian(statxBuffer) & StatxBirthTime) == 0)
            return null;
        var seconds = BinaryPrimitives.ReadInt64LittleEndian(statxBuffer[BirthTimeSecondsOffset..]);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private const int AtFdCwd = -100;
    private const uint StatxBirthTime = 0x800;
    private const int StatxSize = 256;
    private const int BirthTimeSecondsOffset = 0x50;

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags, uint mask, byte[] buffer);

    private IEnumerable<string>? Lines(string path)
    {
        try
        {
            return File.ReadAllLines(PathOf(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A sysfs attribute's value; absent when it's missing, unreadable or blank.</summary>
    private string? Text(string path)
    {
        try
        {
            return File.ReadAllText(PathOf(path)).Trim() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
