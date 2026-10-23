using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.Web.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public static DeviceDto Device(string machineName, DeviceStatus status = DeviceStatus.Online, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), machineName, "corp.example", "Windows", "11", "x64",
            "CPU", 8, 16_384, "10.0.0.5", null, "Servers", ["finance"], false, status, Now, Now);

    public static TenantDto Tenant(string name, TenantStatus status = TenantStatus.Active, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), null, name, null, "ops@example.com", null, SubscriptionPlan.Basic, 25, status, 3, Now, Now);

    public static PagedResult<T> Page<T>(params T[] items) => new(items, 1, 25, items.Length);
}
