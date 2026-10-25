using OSI.Signal9.API.Devices;
using Microsoft.Net.Http.Headers;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.API.Infrastructure;

internal static class Mappings
{
    // Each resource's ETag covers its row version and everything else its DTO shows, so it changes whenever the DTO does.

    public static EntityTagHeaderValue ETag(this Tenant tenant, int deviceCount) =>
        ConditionalRequests.ETag(tenant.Version, deviceCount);

    /// <param name="device">A device loaded with its <see cref="Device.ActiveAgent"/>.</param>
    public static EntityTagHeaderValue ETag(this Device device, DevicePresence presence) =>
        ConditionalRequests.ETag(device.Version, device.ActiveAgent?.Version, presence.StatusOf(device));

    public static EntityTagHeaderValue ETag(this Agent agent, Device device) =>
        ConditionalRequests.ETag(agent.Version, device.Version);

    public static EntityTagHeaderValue ETag(this Command command) => ConditionalRequests.ETag(command.Version);

    public static EntityTagHeaderValue ETag(this TelemetrySample sample) => ConditionalRequests.ETag(sample.Version);

    public static EntityTagHeaderValue ETag(this User user) => ConditionalRequests.ETag(user.Version);

    public static EntityTagHeaderValue ETag(this RoleAssignment assignment) => ConditionalRequests.ETag(assignment.Version);

    /// <summary>Roles have no row version: they change only through migrations, so their content is their version.</summary>
    public static EntityTagHeaderValue ETag(this Role role) =>
        ConditionalRequests.ETag(0, role.Name, role.Description, role.BuiltIn, string.Join(' ', role.Scopes));

    /// <summary>A token's status depends on the time as well as its row, so the ETag covers both.</summary>
    public static EntityTagHeaderValue ETag(this EnrollmentToken token, DateTime now) =>
        ConditionalRequests.ETag(token.Version, token.StatusAt(now));

    public static EnrollmentTokenStatus StatusAt(this EnrollmentToken token, DateTime now) =>
        token.RevokedAt is not null ? EnrollmentTokenStatus.Revoked
        : token.ExpiresAt <= now ? EnrollmentTokenStatus.Expired
        : token.UseCount >= token.MaxUses ? EnrollmentTokenStatus.UsedUp
        : EnrollmentTokenStatus.Active;

    /// <param name="secret">The secret, only in the response that issues it.</param>
    public static EnrollmentTokenDto ToDto(this EnrollmentToken token, DateTime now, string? secret = null) => new(
        token.Id,
        token.TenantId,
        $"/api/v1/tenants/{token.TenantId}/enrollment-tokens/{token.Id}",
        token.Name,
        token.MaxUses,
        token.ExpiresAt.AsUtcOffset(),
        token.StatusAt(now),
        token.CreatedAt.AsUtcOffset(),
        token.RotatedAt.AsUtcOffset(),
        token.RevokedAt.AsUtcOffset(),
        token.UseCount,
        token.LastUsedAt.AsUtcOffset(),
        secret);

    public static UserDto ToDto(this User user) => new(
        user.Id, user.TenantId, user.EntraTenantId, user.EntraObjectId, user.DisplayName, user.CreatedAt.AsUtcOffset());

    public static RoleDto ToDto(this Role role) => new(role.Id, role.Name, role.Description, role.BuiltIn, role.Scopes);

    public static RoleAssignmentDto ToDto(this RoleAssignment assignment) => new(
        assignment.Id, assignment.TenantId, assignment.UserId, assignment.RoleId, assignment.CreatedAt.AsUtcOffset());

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
        device.ActiveAgent?.LastSeenAt.AsUtcOffset(),
        device.RequiresAgentApproval);

    public static AgentDto ToDto(this Agent agent, Device device) => new(
        agent.Id,
        $"/api/v1/agents/{agent.Id}",
        agent.DeviceId,
        device.TenantId,
        agent.EnrollmentTokenId,
        agent.AgentVersion,
        device.ActiveAgentId == agent.Id,
        agent.EnrolledAt.AsUtcOffset(),
        agent.LastSeenAt.AsUtcOffset(),
        agent.RevokedAt?.AsUtcOffset());

    public static AgentCertificateDto ToCertificateDto(this System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => new(
        Server.Identity.ApiApplicationClient.Thumbprint(certificate.RawData),
        new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
        new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));

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
