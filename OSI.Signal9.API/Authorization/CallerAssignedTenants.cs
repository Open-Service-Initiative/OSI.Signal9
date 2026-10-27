using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// The current caller's authority, for <see cref="TenantScope"/>: a Superadministrator has every tenant tree. Anyone
/// else has the tenants where their role assignments grant the scopes the endpoint requires (and any it adds with
/// <see cref="CallerAccess.Require"/>) and their token carries, so everything outside reads as missing.
/// </summary>
public sealed class CallerAssignedTenants(IHttpContextAccessor http, CallerAccess access) : IAssignedTenants
{
    /// <summary>
    /// A Superadministrator has every tenant tree, except through a delegated API application, which holds them to its
    /// subtree (story 81). Read after <see cref="CallerAccess"/> has loaded, which it does before authorization.
    /// </summary>
    public bool IsSuperadministrator => http.HttpContext?.User is { } user && Superadministrator.Is(user) && access.Delegated is null;

    public IReadOnlyCollection<Guid> TenantIds => http.HttpContext is { } context
        ? access.TenantsGranting(context.User,
            context.GetEndpoint()?.Metadata.GetOrderedMetadata<RequireScopeAttribute>().Select(r => r.Scopes) ?? [])
        : [];
}
