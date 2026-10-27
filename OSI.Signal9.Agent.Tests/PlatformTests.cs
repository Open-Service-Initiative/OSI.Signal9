using System.Buffers.Binary;
using OSI.Signal9.Agent.Platforms;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Tests;

/// <summary>
/// What the agent reads from each operating system, behind <see cref="IPlatform"/>. Linux is read from a directory
/// standing in for the filesystem root; macOS has no readers yet, so it reports what it can't read as unknown.
/// </summary>
[TestClass]
public sealed class PlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"signal9-root-{Guid.NewGuid():N}");

    private void Write(string path, string content)
    {
        var file = Path.Combine(_root, path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    [TestMethod]
    public void Each_operating_system_keeps_agent_state_where_its_conventions_put_a_service_s_state()
    {
        // FHS 3.0 §5.8: /var/lib/<package>. Apple: /Library/Application Support/<app> for every user's support files.
        Assert.AreEqual("/var/lib/signal9-agent", new LinuxPlatform().StateDirectory);
        Assert.AreEqual("/Library/Application Support/Signal9 Agent", new MacOSPlatform().StateDirectory);
    }

    [TestMethod]
    public void Each_platform_names_its_family()
    {
        Assert.AreEqual(OsFamily.Windows, new WindowsPlatform().Family);
        Assert.AreEqual(OsFamily.Linux, new LinuxPlatform().Family);
        Assert.AreEqual(OsFamily.MacOS, new MacOSPlatform().Family);
    }

    [TestMethod]
    public void Linux_reads_the_processor_cpu_times_and_memory_from_proc()
    {
        Write("/proc/cpuinfo", "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R) CPU @ 2.20GHz\n");
        Write("/proc/stat", "cpu  100 5 50 800 20 0 25 0 0 0\ncpu0 100 5 50 800 20 0 25 0 0 0\n");
        Write("/proc/meminfo", "MemTotal:       16384000 kB\nMemFree:         1024000 kB\nMemAvailable:    8192000 kB\n");
        var linux = new LinuxPlatform(_root);

        Assert.AreEqual("Intel(R) Xeon(R) CPU @ 2.20GHz", linux.ReadProcessorName());
        // Idle is idle plus iowait; the total is every field.
        Assert.AreEqual(new CpuTimes(820, 1000), linux.ReadCpuTimes());
        Assert.AreEqual(new MemoryReading(16000, 8000), linux.ReadMemory());
    }

    [TestMethod]
    public void Linux_reports_what_proc_lacks_as_unknown()
    {
        var linux = new LinuxPlatform(_root);

        Assert.IsNull(linux.ReadProcessorName());
        Assert.IsNull(linux.ReadCpuTimes());
        Assert.IsNull(linux.ReadMemory());
        Assert.AreEqual(HardwareIdentity.None, linux.ReadHardware());
    }

    [TestMethod]
    public void Linux_reads_the_hardware_identifiers_from_sysfs()
    {
        Write("/sys/class/dmi/id/product_uuid", "4c4c4544-0042-3510-8058-b4c04f4d5a31\n");
        Write("/sys/class/dmi/id/sys_vendor", "Dell Inc.\n");
        Write("/sys/class/dmi/id/product_serial", "5CG1234XYZ\n");

        Assert.AreEqual(new HardwareIdentity(Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31"), "Dell Inc.", "5CG1234XYZ"),
            new LinuxPlatform(_root).ReadHardware());
    }

    [TestMethod]
    public void Malformed_sysfs_values_are_reported_as_absent()
    {
        Write("/sys/class/dmi/id/product_uuid", "not a uuid\n");
        Write("/sys/class/dmi/id/sys_vendor", "  \n");

        Assert.AreEqual(HardwareIdentity.None, new LinuxPlatform(_root).ReadHardware());
    }

    [TestMethod]
    public void Linux_takes_the_os_install_time_from_the_root_filesystem_s_birth_time()
    {
        var installedAt = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

        Assert.AreEqual(installedAt, LinuxPlatform.BirthTime(Statx(mask: 0x800, birthSeconds: installedAt.ToUnixTimeSeconds())));
    }

    [TestMethod]
    public void Linux_reports_no_install_time_when_the_filesystem_records_no_birth_time()
    {
        // The kernel clears STATX_BTIME in stx_mask when the filesystem doesn't record one.
        Assert.IsNull(LinuxPlatform.BirthTime(Statx(mask: 0x7FF, birthSeconds: 1792920600)));
        Assert.IsNull(LinuxPlatform.BirthTime(Statx(mask: 0x800, birthSeconds: 0)));
    }

    [TestMethod]
    public void MacOS_reports_what_the_agent_can_t_read_there_yet_as_unknown()
    {
        var mac = new MacOSPlatform();

        Assert.AreEqual(HardwareIdentity.None, mac.ReadHardware());
        Assert.IsNull(mac.ReadOsInstalledAt());
        Assert.IsNull(mac.ReadProcessorName());
        Assert.IsNull(mac.ReadCpuTimes());
        Assert.IsNull(mac.ReadMemory());
    }

    [TestMethod]
    public void Windows_records_the_install_time_in_seconds_since_the_unix_epoch()
    {
        Assert.AreEqual(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero), WindowsPlatform.FromInstallDate(1792920600));
    }

    [TestMethod]
    [DataRow(null, DisplayName = "Missing")]
    [DataRow(0, DisplayName = "Zero")]
    [DataRow("1792920600", DisplayName = "Not a DWORD")]
    public void A_missing_or_unusable_windows_install_date_is_unknown(object? value)
    {
        Assert.IsNull(WindowsPlatform.FromInstallDate(value));
    }

    /// <summary>A <c>struct statx</c> (linux/stat.h): <c>stx_mask</c> first, <c>stx_btime.tv_sec</c> at 0x50.</summary>
    private static byte[] Statx(uint mask, long birthSeconds)
    {
        var buffer = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, mask);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x50), birthSeconds);
        return buffer;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
