using Microsoft.Extensions.Options;
using OSI.Signal9.API.Data;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.API.Devices;

public sealed class DevicePresenceOptions
{
    public const string SectionName = "DevicePresence";

    /// <summary>A device whose active agent hasn't checked in for this long is Offline.</summary>
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Derives a device's status from its active agent's last check-in. A device with no active agent is Offline.
/// </summary>
public sealed class DevicePresence(IOptions<DevicePresenceOptions> options, TimeProvider time)
{
    private DateTime OnlineCutoff => time.GetUtcNow().UtcDateTime - options.Value.OfflineAfter;

    /// <param name="device">A device loaded with its <see cref="Device.ActiveAgent"/>.</param>
    public DeviceStatus StatusOf(Device device) =>
        device.ActiveAgent is { } agent && agent.LastSeenAt >= OnlineCutoff ? DeviceStatus.Online : DeviceStatus.Offline;

    public IQueryable<Device> WhereStatus(IQueryable<Device> devices, DeviceStatus status)
    {
        var cutoff = OnlineCutoff;
        return status == DeviceStatus.Online
            ? devices.Where(d => d.ActiveAgent != null && d.ActiveAgent.LastSeenAt >= cutoff)
            : devices.Where(d => d.ActiveAgent == null || d.ActiveAgent.LastSeenAt < cutoff);
    }
}
