using OSI.Signal9.API.Agents;
using OSI.Signal9.API.Data;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Infrastructure;

internal static class Mappings
{
    public static TenantDto ToDto(this Tenant tenant, int agentCount) => new(
        tenant.Id,
        tenant.ParentTenantId,
        tenant.Code,
        tenant.Name,
        tenant.Description,
        tenant.ContactEmail,
        tenant.ContactPhone,
        tenant.Plan,
        tenant.MaxAgents,
        tenant.Status,
        agentCount,
        tenant.CreatedAt.AsUtcOffset(),
        tenant.UpdatedAt.AsUtcOffset());

    public static AgentDto ToDto(this Agent agent, AgentPresence presence) => new(
        agent.Id,
        agent.TenantId,
        agent.MachineName,
        agent.Domain,
        agent.OperatingSystem,
        agent.OsVersion,
        agent.Architecture,
        agent.ProcessorName,
        agent.ProcessorCores,
        agent.TotalMemoryMb,
        agent.IpAddress,
        agent.MacAddress,
        agent.AgentVersion,
        agent.GroupName,
        agent.Tags,
        agent.InMaintenance,
        presence.StatusOf(agent),
        agent.FirstSeenAt.AsUtcOffset(),
        agent.LastSeenAt.AsUtcOffset());

    public static AgentCommandDto ToDto(this AgentCommand command) => new(
        command.Id,
        command.AgentId,
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
        sample.AgentId,
        sample.CollectedAt.AsUtcOffset(),
        sample.CpuPercent,
        sample.MemoryUsedMb,
        sample.MemoryTotalMb,
        sample.ProcessCount,
        sample.UptimeSeconds,
        sample.Disks.Select(d => new DiskUsageDto(d.Name, d.TotalBytes, d.FreeBytes)).ToList());
}
