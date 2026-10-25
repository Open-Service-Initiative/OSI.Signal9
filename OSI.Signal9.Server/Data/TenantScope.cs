using Microsoft.EntityFrameworkCore;

namespace OSI.Signal9.Server.Data;

/// <summary>
/// The tenants the current caller has authority over. <see cref="TenantScope"/> shows a Superadministrator every
/// tenant tree, and anyone else only rows owned by the tenants they have role assignments at and their descendants;
/// an empty set shows nothing.
/// </summary>
public interface IAssignedTenants
{
    /// <summary>Whether the caller is a Superadministrator, with authority over every tenant tree (ADR-0006).</summary>
    bool IsSuperadministrator { get; }

    IReadOnlyCollection<Guid> TenantIds { get; }
}

/// <summary>No assigned tenants: the caller sees nothing. The default outside api, which feeds in role assignments.</summary>
public sealed class NoAssignedTenants : IAssignedTenants
{
    public static readonly NoAssignedTenants Instance = new();

    public bool IsSuperadministrator => false;

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
/// Every deliberate bypass of <see cref="TenantScope"/>, by name. Listing them here keeps bypasses rare and visible.
/// Each declares whether its uses are audited (story 70): <see cref="AuditedAttribute"/> bypasses, such as a background
/// job's, go through <c>AuditLog.IgnoreTenantScopeAsync</c>, which records each use; <see cref="NotAuditedAttribute"/>
/// ones are confined to the caller's own identity and covered by the audited events they serve, because auditing every
/// use would write an entry per request.
/// </summary>
public enum TenantScopeBypass
{
    /// <summary>
    /// An agent's requests about itself: its heartbeats, telemetry, pending commands and their results, its certificate,
    /// and whether it's revoked when it asks for a token. Agents aren't assigned to tenants; they reach only the device
    /// whose active agent their access token names (ADR-0005).
    /// </summary>
    [NotAudited("An agent reaches only its own device; the command results it reports are audited.")]
    AgentSelfService,

    /// <summary>
    /// A new agent enrolling with an enrollment token: finding the token by its secret's hash, and creating the device
    /// and agent in the token's tenant. The caller has no access token; the enrollment token is its only authority.
    /// </summary>
    [NotAudited("Reaches only the presented token's tenant, and each enrollment is audited.")]
    Enrollment,

    /// <summary>
    /// Reading the caller's own role assignments to work out their access, which the filter itself depends on. Only
    /// the assignments of the person the access token names are read.
    /// </summary>
    [NotAudited("Runs on every request, and reads only the caller's own assignments.")]
    CallerRoleAssignments,

    /// <summary>
    /// Reading the API application the access token was issued to, to work out its access (#124). Only the
    /// application the token names is read.
    /// </summary>
    [NotAudited("Runs on every request, and reads only the calling application itself.")]
    CallerApiApplication,

    /// <summary>
    /// Finding the tenant an audit entry belongs at (the tenant, device or user a request named, or the signed-in
    /// person's owning tenant). Only a tenant ID is read, and it's never returned to the caller.
    /// </summary>
    [NotAudited("Only files audit entries; nothing it reads reaches the caller.")]
    AuditPlacement,

    /// <summary>
    /// Checking that revoking an Administrator assignment leaves another Administrator at the tenant or above it (story
    /// 23), including at ancestors outside the caller's reach. Only whether one exists is read.
    /// </summary>
    [NotAudited("Answers only yes or no, and the revocation it guards is audited.")]
    AdministratorLockoutGuard,
}

/// <summary>Each use of this <see cref="TenantScopeBypass"/> is recorded in the audit log.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class AuditedAttribute : Attribute;

/// <summary>Uses of this <see cref="TenantScopeBypass"/> aren't audited, for <see cref="Reason"/>.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NotAuditedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
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
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see cref="AuditedAttribute">audited</see>, so it
    /// must go through <c>AuditLog.IgnoreTenantScopeAsync</c>.</exception>
    public static IQueryable<T> IgnoreTenantScope<T>(this IQueryable<T> source, TenantScopeBypass reason) where T : class =>
        IsAudited(reason)
            ? throw new ArgumentException($"{reason} is audited: bypass it through AuditLog.IgnoreTenantScopeAsync.", nameof(reason))
            : Bypass(source, reason);

    /// <summary>Lifts the filter without the audit check, for <c>AuditLog</c>, which records the use itself.</summary>
    internal static IQueryable<T> Bypass<T>(IQueryable<T> source, TenantScopeBypass reason) where T : class =>
        source.IgnoreQueryFilters([Filter]).TagWith($"TenantScope bypass: {reason}");

    /// <summary>Whether each use of <paramref name="reason"/> is recorded in the audit log.</summary>
    public static bool IsAudited(TenantScopeBypass reason) =>
        typeof(TenantScopeBypass).GetField(reason.ToString())?.IsDefined(typeof(AuditedAttribute), inherit: false) ?? true;
}
