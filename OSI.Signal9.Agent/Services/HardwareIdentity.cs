using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// This machine's SMBIOS system identifiers (SMBIOS type 1), which the agent reports at enrollment so a reinstall can
/// rejoin its existing device (#131). Any it can't read are absent. The API decides which are trustworthy: it ignores
/// zeroed and placeholder values, so the agent reports them as they are.
/// </summary>
public sealed record HardwareIdentity(Guid? SmbiosUuid, string? Manufacturer, string? SerialNumber)
{
    public static readonly HardwareIdentity None = new(null, null, null);

    /// <summary>Reads this machine's identifiers: from the firmware table on Windows, from sysfs on Linux.</summary>
    public static HardwareIdentity Read() =>
        OperatingSystem.IsWindows() ? WindowsHardwareIdentity.Read()
        : OperatingSystem.IsLinux() ? LinuxHardwareIdentity.Read()
        : None;
}

/// <summary>Reads the SMBIOS System Information structure from a raw SMBIOS table (SMBIOS spec DSP0134 §7.2).</summary>
public static class SmbiosTable
{
    private const byte SystemInformation = 1;
    private const byte EndOfTable = 127;

    /// <param name="rawSmbiosData">
    /// A Windows <c>RawSMBIOSData</c> buffer: an 8-byte header (calling method, major and minor version, DMI revision,
    /// table length) followed by the structure table.
    /// </param>
    public static HardwareIdentity ReadSystemInformation(ReadOnlySpan<byte> rawSmbiosData)
    {
        if (rawSmbiosData.Length < 8)
            return HardwareIdentity.None;
        var (major, minor) = (rawSmbiosData[1], rawSmbiosData[2]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(rawSmbiosData[4..8]);
        var table = rawSmbiosData[8..];
        if (length < table.Length)
            table = table[..(int)length];

        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var (type, formattedLength) = (table[offset], table[offset + 1]);
            if (formattedLength < 4 || offset + formattedLength > table.Length)
                break;
            var formatted = table.Slice(offset, formattedLength);
            // The structure's strings follow its formatted area, each NUL-terminated, the set ending in a second NUL.
            var stringsStart = offset + formattedLength;
            var stringsEnd = table[stringsStart..].IndexOf([(byte)0, (byte)0]);
            if (stringsEnd < 0)
                break;
            var strings = table.Slice(stringsStart, stringsEnd);

            if (type == SystemInformation)
            {
                // SMBIOS 2.6 and later store the UUID's first three fields little-endian (§7.2.1).
                Guid? uuid = formatted.Length >= 0x18
                    ? new Guid(formatted.Slice(0x08, 16), bigEndian: major < 2 || (major == 2 && minor < 6))
                    : null;
                return new HardwareIdentity(uuid, Text(formatted, strings, 0x04), Text(formatted, strings, 0x07));
            }
            if (type == EndOfTable)
                break;
            offset = stringsStart + stringsEnd + 2;
        }
        return HardwareIdentity.None;
    }

    /// <summary>The string a structure's <paramref name="field"/> refers to, if the structure has that field.</summary>
    private static string? Text(ReadOnlySpan<byte> formatted, ReadOnlySpan<byte> strings, int field) =>
        field < formatted.Length ? StringAt(strings, formatted[field]) : null;

    /// <summary>The structure's <paramref name="index"/>th string (1-based); 0 means the field is unset.</summary>
    private static string? StringAt(ReadOnlySpan<byte> strings, byte index)
    {
        if (index == 0)
            return null;
        foreach (var range in strings.Split((byte)0))
        {
            if (--index == 0)
                return Encoding.ASCII.GetString(strings[range]).Trim() is { Length: > 0 } value ? value : null;
        }
        return null;
    }
}

/// <summary>Reads the identifiers on Windows from the firmware's raw SMBIOS table.</summary>
internal static class WindowsHardwareIdentity
{
    private const uint RawSmbiosProvider = 0x52534D42; // 'RSMB'

    public static HardwareIdentity Read()
    {
        var size = GetSystemFirmwareTable(RawSmbiosProvider, 0, null, 0);
        if (size == 0)
            return HardwareIdentity.None;
        var buffer = new byte[size];
        return GetSystemFirmwareTable(RawSmbiosProvider, 0, buffer, size) == size
            ? SmbiosTable.ReadSystemInformation(buffer)
            : HardwareIdentity.None;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId,
        [Out] byte[]? firmwareTableBuffer, uint bufferSize);
}

/// <summary>Reads the identifiers on Linux from the kernel's DMI attributes in sysfs (readable by root).</summary>
public static class LinuxHardwareIdentity
{
    public const string DmiDirectory = "/sys/class/dmi/id";

    public static HardwareIdentity Read(string directory = DmiDirectory) => new(
        Guid.TryParse(ReadAttribute(directory, "product_uuid"), out var uuid) ? uuid : null,
        ReadAttribute(directory, "sys_vendor"),
        ReadAttribute(directory, "product_serial"));

    private static string? ReadAttribute(string directory, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(directory, name)).Trim() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Absent on machines without DMI, and root-only for some attributes: report it as unknown.
            return null;
        }
    }
}
