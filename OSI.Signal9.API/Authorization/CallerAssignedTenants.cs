using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// The current caller's authority, from their access token: a Superadministrator has every tenant tree. Anyone else
/// has no assigned tenants until RBAC feeds role assignments in (#114).
/// </summary>
public sealed class CallerAssignedTenants(IHttpContextAccessor http) : IAssignedTenants
{
    public bool IsSuperadministrator => http.HttpContext?.User is { } user && Superadministrator.Is(user);

    public IReadOnlyCollection<Guid> TenantIds => [];
}
