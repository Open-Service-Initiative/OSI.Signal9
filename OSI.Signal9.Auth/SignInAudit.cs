using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.Auth;

/// <summary>
/// Audits each sign-in at Entra through auth (#115): stored at the person's owning tenant when they have a user, and
/// otherwise deployment-level. A refused sign-in is recorded as failed, with the reason, and counts toward the
/// repeated-failure audit (story 121).
/// </summary>
internal static class SignInAudit
{
    public static Task SucceededAsync(HttpContext context, ClaimsPrincipal signIn, AuthenticationProperties? properties,
        bool superadministrator) =>
        WriteAsync(context, signIn, properties, AuditOutcome.Succeeded, detail: null, superadministrator);

    public static async Task FailedAsync(HttpContext context, ClaimsPrincipal? signIn, AuthenticationProperties? properties,
        EntraSignIn.Rejection rejection)
    {
        await WriteAsync(context, signIn, properties, AuditOutcome.Failed, rejection.ToString(), superadministrator: false);
        await context.RequestServices.GetRequiredService<AuthenticationFailures>()
            .RecordAsync(context, ClientIdOf(properties), context.RequestAborted);
    }

    private static async Task WriteAsync(HttpContext context, ClaimsPrincipal? signIn, AuthenticationProperties? properties,
        AuditOutcome outcome, string? detail, bool superadministrator)
    {
        var log = context.RequestServices.GetRequiredService<AuditLog>();
        var person = signIn is null ? null : EntraPerson.From(signIn);
        await log.WriteAsync(new AuditEntry
        {
            Action = AuditActions.SignIn,
            Outcome = outcome,
            TenantId = person is null ? null : await log.TenantOfPersonAsync(person.TenantId, person.ObjectId, context.RequestAborted),
            ActorKind = person is null ? AuditActorKind.Anonymous : AuditActorKind.Person,
            ActorSubject = person?.Subject,
            ActorClientId = ClientIdOf(properties),
            ActorSuperadministrator = superadministrator,
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            Detail = detail,
        }, context.RequestAborted);
    }

    /// <summary>The client whose authorize request sent the person to Entra.</summary>
    private static string? ClientIdOf(AuthenticationProperties? properties)
    {
        if (properties?.RedirectUri is not { } returnUrl
            || !returnUrl.StartsWith(AuthorizationEndpoint.ReturnPath, StringComparison.Ordinal))
            return null;
        var query = QueryHelpers.ParseQuery(returnUrl[(AuthorizationEndpoint.ReturnPath.Length - 1)..]);
        return query.TryGetValue("client_id", out var clientId) ? clientId.ToString() : null;
    }
}
