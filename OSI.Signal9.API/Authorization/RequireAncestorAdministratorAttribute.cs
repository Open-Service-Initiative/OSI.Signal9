using Microsoft.AspNetCore.Authorization;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// Marks an endpoint only an Administrator above the target tenant, or a Superadministrator, may use: an Administrator
/// can't use it at their own assigned tenant (stories 21-22, 64). The target tenant is the route value named
/// <see cref="TenantRouteValue"/>. Shown in the OpenAPI document (story 65).
/// </summary>
/// <remarks>
/// A tenant outside the caller's reach passes this requirement, so the endpoint, which reads the tenant through the
/// tenant filter, answers 404 as it would for any tenant the caller can't see, rather than 403 revealing it exists.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireAncestorAdministratorAttribute(string tenantRouteValue = "tenantId")
    : Attribute, IAuthorizationRequirement, IAuthorizationRequirementData, IDocumentedRequirement
{
    /// <summary>The route value holding the target tenant's ID.</summary>
    public string TenantRouteValue { get; } = tenantRouteValue;

    public string Name => "ancestor-administrator";

    public string Description =>
        "Needs an Administrator role assignment at an ancestor of the target tenant, or a Superadministrator: an Administrator can't use it at their own assigned tenant.";

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return this;
    }

    public override string ToString() => "RequireAncestorAdministrator";
}

/// <summary>
/// Decides <see cref="RequireAncestorAdministratorAttribute"/> for the request (its <see cref="HttpContext"/> is the
/// resource), against the caller's live role assignments. Fails closed when the route has no target tenant.
/// </summary>
internal sealed class RequireAncestorAdministratorHandler : AuthorizationHandler<RequireAncestorAdministratorAttribute>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        RequireAncestorAdministratorAttribute requirement)
    {
        if (context.Resource is HttpContext http
            && Guid.TryParse(http.GetRouteValue(requirement.TenantRouteValue)?.ToString(), out var tenantId)
            && await AncestorAdministrator.AdmitsAsync(http, tenantId))
            context.Succeed(requirement);
    }
}

/// <summary>The rule behind <see cref="RequireAncestorAdministratorAttribute"/>, for actions that apply it only sometimes.</summary>
public static class AncestorAdministrator
{
    /// <summary>
    /// Whether the caller may act at <paramref name="tenantId"/> as an Administrator above it: a Superadministrator, an
    /// Administrator at one of its ancestors, or someone who can't reach it at all (the tenant filter then hides it).
    /// </summary>
    public static async Task<bool> AdmitsAsync(HttpContext http, Guid tenantId)
    {
        var services = http.RequestServices;
        if (services.GetRequiredService<IAssignedTenants>().IsSuperadministrator)
            return true;

        var db = services.GetRequiredService<Signal9DbContext>();
        var access = services.GetRequiredService<CallerAccess>();
        await access.LoadAsync(db, http.User, http.RequestAborted);
        return await access.IsAdministratorAboveAsync(db, tenantId, http.RequestAborted)
            || !await access.ReachesAsync(db, tenantId, http.RequestAborted);
    }
}
