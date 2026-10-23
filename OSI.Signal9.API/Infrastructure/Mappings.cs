using OSI.Signal9.API.Devices;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Infrastructure;

internal static class Mappings
{
    public static TenantDto ToDto(this Tenant tenant, int deviceCount) => new(
        tenant.Id,
        tenant.ParentTenantId,
        tenant.Name,
        tenant.Description,
        tenant.ContactEmail,
        tenant.ContactPhone,
        tenant.Plan,
        tenant.MaxDevices,
        tenant.Status,
        deviceCount,
        tenant.CreatedAt.AsUtcOffset(),
        tenant.UpdatedAt.AsUtcOffset());

    /// <param name="device">A device loaded with its <see cref="Device.ActiveAgent"/>.</param>
    public static DeviceDto ToDto(this Device device, DevicePresence presence) => new(
        device.Id,
        device.TenantId,
        device.ActiveAgentId,
        device.MachineName,
        device.Domain,
        device.OperatingSystem,
        device.OsVersion,
        device.Architecture,
        device.ProcessorName,
        device.ProcessorCores,
        device.TotalMemoryMb,
        device.IpAddress,
        device.MacAddress,
        device.GroupName,
        device.Tags,
        device.MaintenanceMode,
        presence.StatusOf(device),
        device.CreatedAt.AsUtcOffset(),
        device.ActiveAgent?.LastSeenAt.AsUtcOffset());

    public static AgentDto ToDto(this Agent agent, Device device) => new(
        agent.Id,
        agent.DeviceId,
        device.TenantId,
        agent.AgentVersion,
        device.ActiveAgentId == agent.Id,
        agent.EnrolledAt.AsUtcOffset(),
        agent.LastSeenAt.AsUtcOffset());

    public static CommandDto ToDto(this Command command) => new(
        command.Id,
        command.DeviceId,
        $"/api/v1/devices/{command.DeviceId}/commands/{command.Id}",
        command.Type,
        command.Parameters,
        command.Priority,
        command.TimeoutSeconds,
        command.Status,
        command.CreatedAt.AsUtcOffset(),
        command.CompletedAt.AsUtcOffset(),
        command.Output,
        command.Error);

    public static TelemetrySampleDto ToDto(this TelemetrySample sample) => new(
        sample.Id,
        sample.DeviceId,
        sample.CollectedAt.AsUtcOffset(),
        sample.CpuPercent,
        sample.MemoryUsedMb,
        sample.MemoryTotalMb,
        sample.ProcessCount,
        sample.UptimeSeconds,
        sample.Disks.Select(d => new DiskUsageDto(d.Name, d.TotalBytes, d.FreeBytes)).ToList());
}
