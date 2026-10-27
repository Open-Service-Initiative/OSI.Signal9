using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// Refuses a token that lacks a required scope with 403 and <c>WWW-Authenticate: Bearer error="insufficient_scope",
/// scope="…"</c> (RFC 6750 §3.1), so the client learns which scope to ask for.
/// </summary>
internal static class InsufficientScope
{
    public static AuthenticationProperties Properties(IEnumerable<string> scopes) => new(new Dictionary<string, string?>
    {
        [OpenIddictValidationAspNetCoreConstants.Properties.Error] = Errors.InsufficientScope,
        [OpenIddictValidationAspNetCoreConstants.Properties.ErrorDescription] = "The access token lacks a scope this request requires.",
        [OpenIddictValidationAspNetCoreConstants.Properties.Scope] = string.Join(' ', scopes),
    });

    /// <summary>Refuses the request from inside an action, for a scope that depends on what is asked for.</summary>
    public static ForbidResult Forbid(params string[] scopes) =>
        new(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Properties(scopes));
}

/// <summary>
/// Turns a failed <see cref="RequireScopeAttribute"/> into an <see cref="InsufficientScope"/> refusal, and a person's
/// failed <see cref="RequireRecentMfaAttribute"/> into the <see cref="RecentMfa"/> step-up challenge.
/// </summary>
internal sealed class AuthorizationRefusalHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        // Only a requirement whose scopes the token lacks is insufficient_scope; one refused although the token has a
        // scope (deployment.* for someone who isn't a Superadministrator) is a plain 403.
        var missing = authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<RequireScopeAttribute>()
            .Where(r => !r.Scopes.Any(context.User.HasScope))
            .SelectMany(r => r.Scopes).Distinct().ToList();
        if (authorizeResult.Forbidden && missing is { Count: > 0 })
            return context.ForbidAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, InsufficientScope.Properties(missing));

        // Signing in again helps only a person whose token lacks nothing else; an application gets the plain 403.
        var failed = authorizeResult.AuthorizationFailure?.FailedRequirements.ToList();
        if (authorizeResult.Forbidden && failed is { Count: > 0 } && failed.All(r => r is RequireRecentMfaAttribute)
            && RecentMfa.IsPerson(context.User))
            return RecentMfa.ChallengeAsync(context);

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
