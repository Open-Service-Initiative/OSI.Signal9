using System.Text;
using OSI.Signal9.Agent.Services;

namespace OSI.Signal9.Agent.Tests;

/// <summary>
/// The hardware identifiers an agent reports at enrollment (#131), which identify a reinstalled agent's device: the
/// SMBIOS system UUID, manufacturer and serial, read from the firmware table on Windows and from sysfs on Linux.
/// </summary>
[TestClass]
public sealed class HardwareIdentityTests : IDisposable
{
    private readonly string _sysfs = Path.Combine(Path.GetTempPath(), $"signal9-dmi-{Guid.NewGuid():N}");

    /// <summary>
    /// A Windows RawSMBIOSData buffer (an 8-byte header with the SMBIOS version, then the structure table) holding a
    /// BIOS structure (type 0) and then a System Information structure (type 1) with the given UUID bytes as stored.
    /// </summary>
    private static byte[] RawSmbios(byte major, byte minor, byte[] uuid, params string[] strings)
    {
        var table = new List<byte>();
        // Type 0, length 4, handle 0, no strings: formatted area then the double-NUL terminator.
        table.AddRange([0x00, 0x04, 0x00, 0x00, 0x00, 0x00]);
        // Type 1, length 0x1B: manufacturer #1, product #2, version #3, serial #4, UUID, wake-up type, SKU, family.
        table.AddRange([0x01, 0x1B, 0x01, 0x00, 0x01, 0x02, 0x03, 0x04]);
        table.AddRange(uuid);
        table.AddRange([0x06, 0x00, 0x00]);
        foreach (var value in strings)
        {
            table.AddRange(Encoding.ASCII.GetBytes(value));
            table.Add(0);
        }
        table.Add(0);
        // End-of-table structure (type 127).
        table.AddRange([0x7F, 0x04, 0x02, 0x00, 0x00, 0x00]);

        var header = new byte[8];
        header[1] = major;
        header[2] = minor;
        BitConverter.TryWriteBytes(header.AsSpan(4), table.Count);
        return [.. header, .. table];
    }

    private static readonly byte[] StoredUuid =
        [0x44, 0x45, 0x4C, 0x4C, 0x42, 0x00, 0x10, 0x35, 0x80, 0x58, 0xB4, 0xC0, 0x4F, 0x4D, 0x5A, 0x31];

    [TestMethod]
    public void Reads_the_system_information_from_a_raw_SMBIOS_table()
    {
        var raw = RawSmbios(3, 4, StoredUuid, "Dell Inc.", "Latitude 7440", "1.0", "5CG1234XYZ");

        var identity = SmbiosTable.ReadSystemInformation(raw);

        // SMBIOS 2.6 and later store the first three UUID fields little-endian, as Windows and Linux both print it.
        Assert.AreEqual(Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31"), identity.SmbiosUuid);
        Assert.AreEqual("Dell Inc.", identity.Manufacturer);
        Assert.AreEqual("5CG1234XYZ", identity.SerialNumber);
    }

    [TestMethod]
    public void Reads_the_UUID_in_network_byte_order_before_SMBIOS_2_6()
    {
        var raw = RawSmbios(2, 4, StoredUuid, "Dell Inc.", "Latitude", "1.0", "5CG1234XYZ");

        Assert.AreEqual(Guid.Parse("44454c4c-4200-1035-8058-b4c04f4d5a31"), SmbiosTable.ReadSystemInformation(raw).SmbiosUuid);
    }

    [TestMethod]
    public void A_table_without_system_information_or_one_cut_short_yields_nothing()
    {
        var raw = RawSmbios(3, 4, StoredUuid, "Dell Inc.", "Latitude", "1.0", "5CG1234XYZ");

        Assert.AreEqual(new HardwareIdentity(null, null, null), SmbiosTable.ReadSystemInformation(raw.AsSpan(0, 20)));
        Assert.AreEqual(new HardwareIdentity(null, null, null), SmbiosTable.ReadSystemInformation([]));
    }

    [TestMethod]
    public void An_unset_string_is_reported_as_absent()
    {
        var raw = RawSmbios(3, 4, StoredUuid, "Dell Inc.");

        var identity = SmbiosTable.ReadSystemInformation(raw);

        Assert.AreEqual("Dell Inc.", identity.Manufacturer);
        Assert.IsNull(identity.SerialNumber);
    }

    [TestMethod]
    public void Reads_the_identifiers_from_sysfs_on_Linux()
    {
        Directory.CreateDirectory(_sysfs);
        File.WriteAllText(Path.Combine(_sysfs, "product_uuid"), "4c4c4544-0042-3510-8058-b4c04f4d5a31\n");
        File.WriteAllText(Path.Combine(_sysfs, "sys_vendor"), "Dell Inc.\n");
        File.WriteAllText(Path.Combine(_sysfs, "product_serial"), "5CG1234XYZ\n");

        var identity = LinuxHardwareIdentity.Read(_sysfs);

        Assert.AreEqual(new HardwareIdentity(Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31"), "Dell Inc.", "5CG1234XYZ"), identity);
    }

    [TestMethod]
    public void Missing_or_malformed_sysfs_values_are_reported_as_absent()
    {
        Directory.CreateDirectory(_sysfs);
        File.WriteAllText(Path.Combine(_sysfs, "product_uuid"), "not a uuid\n");

        Assert.AreEqual(new HardwareIdentity(null, null, null), LinuxHardwareIdentity.Read(_sysfs));
    }

    [TestMethod]
    public void The_enrollment_request_carries_the_hardware_identifiers()
    {
        var identity = new HardwareIdentity(Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31"), "Dell Inc.", "5CG1234XYZ");

        var request = new SystemInfoProvider(() => identity).GetEnrollment("token", "certificate");

        Assert.AreEqual(identity.SmbiosUuid, request.SmbiosUuid);
        Assert.AreEqual("Dell Inc.", request.Manufacturer);
        Assert.AreEqual("5CG1234XYZ", request.SerialNumber);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sysfs))
            Directory.Delete(_sysfs, recursive: true);
    }
}
