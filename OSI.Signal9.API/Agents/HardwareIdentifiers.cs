using System.Linq.Expressions;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Agents;

/// <summary>
/// The hardware identifiers an agent reports at enrollment, normalized for identification (story 104): values that are
/// zeroed, blank or known firmware placeholders are dropped, so cloned or generic machines never match each other.
/// Manufacturer and serial are trimmed and upper-cased, and only count together.
/// </summary>
internal sealed record HardwareIdentifiers(Guid? SmbiosUuid, string? Manufacturer, string? SerialNumber)
{
    // Placeholder UUIDs firmware ships with, beyond those whose bytes are all alike (zeroed or all FF). The first is
    // the AMI default many boards report unchanged, in both byte orders.
    private static readonly HashSet<Guid> PlaceholderUuids =
    [
        Guid.Parse("03000200-0400-0500-0006-000700080009"),
        Guid.Parse("00020003-0004-0005-0006-000700080009"),
        Guid.Parse("12345678-1234-5678-90ab-cddeefaabbcc"),
    ];

    // Placeholder strings firmware leaves in the SMBIOS system fields, compared case-insensitively after trimming.
    private static readonly HashSet<string> PlaceholderStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.", "To Be Filled By OEM", "Default string", "Default", "System Serial Number",
        "System manufacturer", "System Product Name", "System Version", "Chassis Serial Number", "Chassis Manufacture",
        "Base Board Serial Number", "Serial Number", "SerialNumber", "Not Specified", "Not Applicable", "Not Available",
        "None", "N/A", "NA", "Unknown", "Undefined", "Invalid", "Empty", "Null", "OEM", "O.E.M.", "OEM_Serial",
        "Manufacturer", "Default_String", "123456789", "1234567890", "0123456789",
    };

    /// <summary>A device's stored identifiers, which were normalized when it was created or matched.</summary>
    public static HardwareIdentifiers Of(Device device) => new(device.SmbiosUuid, device.Manufacturer, device.SerialNumber);

    /// <summary>Whether there's anything to match on: an SMBIOS UUID, or a manufacturer and serial.</summary>
    public bool IsEmpty => SmbiosUuid is null && (Manufacturer is null || SerialNumber is null);

    /// <summary>
    /// Whether <paramref name="other"/> is the same machine: the same SMBIOS UUID, or the same manufacturer and serial
    /// (a serial alone never matches). Machine names play no part (story 106).
    /// </summary>
    public bool Matches(HardwareIdentifiers other) =>
        (SmbiosUuid != null && SmbiosUuid == other.SmbiosUuid)
        || (SerialNumber != null && Manufacturer != null && SerialNumber == other.SerialNumber && Manufacturer == other.Manufacturer);

    /// <summary><see cref="Matches"/> as a query filter: the devices that are the same machine as these identifiers.</summary>
    public Expression<Func<Device, bool>> MatchingDevices()
    {
        var (uuid, manufacturer, serial) = (SmbiosUuid, Manufacturer, SerialNumber);
        return d => (uuid != null && d.SmbiosUuid == uuid)
            || (serial != null && manufacturer != null && d.SerialNumber == serial && d.Manufacturer == manufacturer);
    }

    public static HardwareIdentifiers From(Guid? smbiosUuid, string? manufacturer, string? serialNumber)
    {
        var uuid = smbiosUuid is { } value && !IsPlaceholder(value) ? value : (Guid?)null;
        var maker = Normalize(manufacturer);
        var serial = Normalize(serialNumber);
        return maker is null || serial is null ? new(uuid, null, null) : new(uuid, maker, serial);
    }

    private static bool IsPlaceholder(Guid uuid)
    {
        Span<byte> bytes = stackalloc byte[16];
        uuid.TryWriteBytes(bytes);
        return !bytes.ContainsAnyExcept(bytes[0]) || PlaceholderUuids.Contains(uuid);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || PlaceholderStrings.Contains(trimmed))
            return null;
        // A run of one character, such as 0000000 or ........, identifies nothing.
        if (!trimmed.AsSpan().ContainsAnyExcept(trimmed[0]))
            return null;
        return trimmed.ToUpperInvariant();
    }
}
