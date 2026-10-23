using Microsoft.EntityFrameworkCore;

namespace OSI.Signal9.Server.Data;

/// <summary>
/// The tenants the current caller has role assignments at. <see cref="TenantScope"/> shows the caller only rows owned
/// by these tenants and their descendants; an empty set shows nothing.
/// </summary>
public interface IAssignedTenants
{
    IReadOnlyCollection<Guid> TenantIds { get; }
}

/// <summary>No assigned tenants: the caller sees nothing. The default until RBAC feeds role assignments in (#114).</summary>
public sealed class NoAssignedTenants : IAssignedTenants
{
    public static readonly NoAssignedTenants Instance = new();

    public IReadOnlyCollection<Guid> TenantIds => [];
}

/// <summary>An entity owned directly by a tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>An entity owned by a device, and so by the device's tenant, whichever tenant that is now.</summary>
public interface IDeviceOwned
{
    Guid DeviceId { get; }
}

/// <summary>
/// Every deliberate bypass of <see cref="TenantScope"/>, by name. Listing them here keeps bypasses rare and visible, and
/// lets each be audited once the audit log exists (story 70).
/// </summary>
public enum TenantScopeBypass
{
    /// <summary>
    /// An agent's requests about itself: its heartbeats, telemetry, pending commands and their results, and registering.
    /// Agents aren't assigned to tenants; they reach only the records of the device named by the request.
    /// </summary>
    // TODO(auth): scope agents to their own device once agent identity comes from the token (ADR-0005).
    AgentSelfService,
}

/// <summary>
/// The central tenant filter (ADR-0001): an EF Core named query filter on every tenant-owned entity, keeping rows whose
/// owning tenant is a descendant (in the closure table) of one of the caller's <see cref="IAssignedTenants"/>.
/// </summary>
public static class TenantScope
{
    /// <summary>The query filter's name.</summary>
    public const string Filter = nameof(TenantScope);

    /// <summary>
    /// Lifts <see cref="TenantScope"/> from the whole query (every entity in it, not just <paramref name="source"/>'s),
    /// for <paramref name="reason"/>, which is tagged onto the SQL. The only way production code may ignore it.
    /// </summary>
    public static IQueryable<T> IgnoreTenantScope<T>(this IQueryable<T> source, TenantScopeBypass reason) where T : class =>
        source.IgnoreQueryFilters([Filter]).TagWith($"TenantScope bypass: {reason}");
}
