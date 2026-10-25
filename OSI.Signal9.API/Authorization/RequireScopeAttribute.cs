using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// Requires an access token carrying one of these scopes from the catalogue (<see cref="Server.Identity.Scopes"/>). Every
/// endpoint has one or is explicitly <see cref="AllowAnonymousAttribute"/> (story 62). Put it on actions, not
/// controllers: attributes at both levels must all be satisfied.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireScopeAttribute(params string[] scopes)
    : AuthorizeAttribute, IAuthorizationRequirement, IAuthorizationRequirementData
{
    /// <summary>The scopes, any one of which satisfies the requirement.</summary>
    public IReadOnlyList<string> Scopes { get; } = scopes;

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return this;
    }

    public override string ToString() => $"RequireScope({string.Join(" or ", Scopes)})";
}

/// <summary>
/// Succeeds when the caller's access token carries one of the required scopes, and the API application it was issued
/// to still holds it. A <c>deployment.*</c> scope counts only for a Superadministrator (#63): an API application can
/// hold it, but only acting for one, whose delegated token reaches it as the dashboard's would (story 72).
/// </summary>
internal sealed class RequireScopeHandler(IHttpContextAccessor accessor) : AuthorizationHandler<RequireScopeAttribute>
{
    public const string DeploymentPrefix = "deployment.";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RequireScopeAttribute requirement)
    {
        // Actions may authorize without a resource; fails closed outside an HTTP request.
        if ((context.Resource as HttpContext ?? accessor.HttpContext) is not { } http)
            return;
        var services = http.RequestServices;
        var access = services.GetRequiredService<CallerAccess>();
        await access.LoadAsync(services.GetRequiredService<Signal9DbContext>(), context.User, http.RequestAborted);

        if (requirement.Scopes.Any(scope => context.User.HasScope(scope) && access.Permits(scope)
                && (!scope.StartsWith(DeploymentPrefix, StringComparison.Ordinal) || IsSuperadministrator(http, context.User))))
            context.Succeed(requirement);
    }

    // The person is a Superadministrator, whether through the dashboard or a delegated application.
    private static bool IsSuperadministrator(HttpContext http, System.Security.Claims.ClaimsPrincipal user) =>
        http.RequestServices.GetRequiredService<IAssignedTenants>().IsSuperadministrator || Server.Identity.Superadministrator.Is(user);
}
