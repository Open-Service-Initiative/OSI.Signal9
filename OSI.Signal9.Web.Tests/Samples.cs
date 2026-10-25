using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.Web.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public static DeviceDto Device(string machineName, DeviceStatus status = DeviceStatus.Online, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), machineName, "corp.example", "Windows", "11", "x64",
            "CPU", 8, 16_384, "10.0.0.5", null, "Servers", ["finance"], false, status, Now, Now, false);

    public static TenantDto Tenant(string name, TenantStatus status = TenantStatus.Active, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), null, name, null, "ops@example.com", null, SubscriptionPlan.Basic, 25, status, 3, Now, Now);

    public static AuditEntryDto AuditEntry(string action, string? tenantName = "Contoso", Guid? deviceId = null,
        string? deviceName = null, string? actorName = "Alex Wilber", AuditOutcome outcome = AuditOutcome.Succeeded,
        bool superadministrator = false, string? detail = null) =>
        new(Guid.NewGuid(), Now, action, outcome,
            new AuditActorDto(AuditActorKind.Person, "tid:oid", "web", superadministrator, actorName is null ? null : Guid.NewGuid(), actorName),
            tenantName is null ? null : Guid.NewGuid(), tenantName, "command", Guid.NewGuid(), deviceId, deviceName,
            null, null, null, null, detail, "203.0.113.1");

    public static UserDto User(string displayName, Guid tenantId, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenantId, Guid.NewGuid(), Guid.NewGuid(), displayName, Now);

    public static RoleDto Role(string name, Guid? id = null) => new(id ?? Guid.NewGuid(), name, $"The {name} role.", true, []);

    public static RoleAssignmentDto RoleAssignment(Guid tenantId, Guid userId, Guid roleId, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenantId, userId, roleId, Now);

    public static EnrollmentTokenDto EnrollmentToken(string name, Guid tenantId, Guid? id = null,
        EnrollmentTokenStatus status = EnrollmentTokenStatus.Active, int? maxUses = null, string? secret = null,
        int useCount = 0, DateTimeOffset? lastUsedAt = null)
    {
        var tokenId = id ?? Guid.NewGuid();
        return new(tokenId, tenantId, $"/api/v1/tenants/{tenantId}/enrollment-tokens/{tokenId}", name, maxUses, null, status,
            Now, null, status == EnrollmentTokenStatus.Revoked ? Now : null, useCount, lastUsedAt, secret);
    }

    public static ApiApplicationDto ApiApplication(string name, Guid tenantId, Guid roleId, string method, Guid? id = null,
        params string[] scopes)
    {
        var applicationId = id ?? Guid.NewGuid();
        return new(applicationId, tenantId, applicationId.ToString(), name, roleId, scopes, method, null, Now);
    }

    public static PagedResult<T> Page<T>(params T[] items) => new(items, 1, 25, items.Length);
}
