using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// Requires a person who performed MFA within the last 15 minutes, judged from the access token's <c>auth_time</c> and
/// <c>amr</c> (stories 45, 63). A delegated token without it gets 401 with RFC 9470's
/// <c>insufficient_user_authentication</c> challenge and <c>max_age=900</c>, so the client signs the person in again.
/// An application acting as itself has no person to step up and is refused with 403 until its grant was approved with
/// MFA (#63). Use it alongside <see cref="RequireScopeAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireRecentMfaAttribute : Attribute, IAuthorizationRequirement, IAuthorizationRequirementData,
    IDocumentedRequirement
{
    /// <summary>How recent the MFA must be.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public string Name => "recent-mfa";

    public string Description =>
        "A person who performed MFA within the last 15 minutes. Otherwise the API answers 401 with "
        + "`WWW-Authenticate: Bearer error=\"insufficient_user_authentication\", max_age=900` (RFC 9470): sign the person "
        + "in again with authorization code and `max_age=900`. An application acting as itself is refused.";

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return this;
    }

    public override string ToString() => "RequireRecentMfa";
}

/// <summary>Succeeds for a person whose token says they performed MFA (<c>amr</c>) at most 15 minutes ago (<c>auth_time</c>).</summary>
internal sealed class RequireRecentMfaHandler(TimeProvider time) : AuthorizationHandler<RequireRecentMfaAttribute>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RequireRecentMfaAttribute requirement)
    {
        if (RecentMfa.IsPerson(context.User)
            && context.User.FindAll(Claims.AuthenticationMethodReference).Any(claim => claim.Value == "mfa")
            && long.TryParse(context.User.FindFirst(Claims.AuthenticationTime)?.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var seconds)
            && time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds) <= RequireRecentMfaAttribute.MaxAge)
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>The step-up challenge (RFC 9470 §3) for a person whose MFA isn't recent enough.</summary>
internal static class RecentMfa
{
    public const string Error = "insufficient_user_authentication";

    public static readonly long MaxAgeSeconds = (long)RequireRecentMfaAttribute.MaxAge.TotalSeconds;

    /// <summary>Whether the token was issued for a person, who can sign in again, rather than an application.</summary>
    public static bool IsPerson(ClaimsPrincipal caller) => PersonSubject.TryParse(caller, out _, out _);

    /// <summary>
    /// Answers 401 with <c>WWW-Authenticate: Bearer error="insufficient_user_authentication", max_age=900</c> and a
    /// problem details body.
    /// </summary>
    public static Task ChallengeAsync(HttpContext context)
    {
        const string description = "This action needs a multi-factor authentication within the last 15 minutes.";
        context.Response.Headers.WWWAuthenticate = string.Create(CultureInfo.InvariantCulture,
            $"Bearer error=\"{Error}\", error_description=\"{description}\", max_age={MaxAgeSeconds}");
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Recent MFA required",
            detail: description + $" Sign in again with max_age={MaxAgeSeconds}.").ExecuteAsync(context);
    }
}

/// <summary>Documents the step-up challenge on the 401 of every operation that needs a recent MFA.</summary>
internal sealed class RecentMfaResponses : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<RequireRecentMfaAttribute>().Any())
            return Task.CompletedTask;

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["401"] = new OpenApiResponse
        {
            Description = "No valid access token, or the person's MFA isn't recent enough: `WWW-Authenticate: Bearer "
                + $"error=\"{RecentMfa.Error}\", max_age={RecentMfa.MaxAgeSeconds}` (RFC 9470).",
        };
        operation.Responses["403"] = new OpenApiResponse
        {
            Description = "The access token lacks a required scope, or was issued to an application acting as itself.",
        };
        return Task.CompletedTask;
    }
}
