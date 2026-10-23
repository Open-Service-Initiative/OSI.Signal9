using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;

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

/// <summary>Succeeds when the caller's access token carries one of the required scopes.</summary>
internal sealed class RequireScopeHandler : AuthorizationHandler<RequireScopeAttribute>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RequireScopeAttribute requirement)
    {
        if (requirement.Scopes.Any(context.User.HasScope))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
