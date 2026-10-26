using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using OSI.Signal9.Auth.Pages;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Auth;

/// <summary>
/// A person's consent to a client acting for them (story 87): asked on auth the first time, and again whenever the
/// client asks for scopes it wasn't given, then kept as a permanent authorization. Only the person themselves can
/// consent, in their own session: there's no admin consent on behalf of users. Clients registered with implicit consent,
/// Signal9's own <c>web</c>, never ask.
/// </summary>
public sealed class Consent(
    IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations, IAntiforgery antiforgery,
    AuditLog audit)
{
    /// <summary>The consent screen's answer: <c>accept</c> or <c>deny</c>.</summary>
    public const string DecisionField = "consent";

    /// <summary>The antiforgery token's form field, which the screen posts back with its answer.</summary>
    public const string AntiforgeryField = "__RequestVerificationToken";

    /// <summary>Whether a request parameter is the consent screen's own, not the client's.</summary>
    public static bool IsOwnField(string name) => name is DecisionField or AntiforgeryField;

    /// <summary>What the authorization endpoint does next.</summary>
    public abstract record Outcome
    {
        /// <summary>Issue the code: the client needs no consent, or has it.</summary>
        public sealed record Granted : Outcome;

        /// <summary>Show the person the consent screen.</summary>
        public sealed record Ask(IResult Page) : Outcome;

        public sealed record Declined : Outcome;

        /// <summary>Consent is needed, but the client asked for no interaction (<c>prompt=none</c>).</summary>
        public sealed record Required : Outcome;

        /// <summary>The answer didn't come from the person's own consent screen.</summary>
        public sealed record Refused(IResult Result) : Outcome;
    }

    public async Task<Outcome> DecideAsync(HttpContext context, OpenIddictRequest request, ClaimsIdentity identity,
        EntraPerson person, IEnumerable<KeyValuePair<string, StringValues>> parameters)
    {
        var cancellationToken = context.RequestAborted;
        var application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken)
            ?? throw new InvalidOperationException("OpenIddict validated a client that doesn't exist.");
        if (await applications.GetConsentTypeAsync(application, cancellationToken) == ConsentTypes.Implicit)
            return new Outcome.Granted();

        var client = await applications.GetIdAsync(application, cancellationToken);
        var scopes = request.GetScopes();
        var decision = context.Request.HasFormContentType ? context.Request.Form[DecisionField].ToString() : "";
        if (decision is "accept" or "deny")
        {
            // Only the screen auth showed in this person's session can answer it (OWASP CSRF prevention).
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return new Outcome.Refused(Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Consent not confirmed", detail: "Answer the consent screen auth showed you."));
            }
            if (decision == "deny")
                return new Outcome.Declined();
            await GrantAsync(context, application, client, identity, person, scopes);
            return new Outcome.Granted();
        }

        // A consent covering every requested scope is reused, unless the client asks for the screen (prompt=consent).
        if (!request.HasPromptValue(PromptValues.Consent))
        {
            await foreach (var _ in authorizations.FindAsync(person.Subject, client, Statuses.Valid,
                               AuthorizationTypes.Permanent, scopes, cancellationToken))
                return new Outcome.Granted();
        }
        if (request.HasPromptValue(PromptValues.None))
            return new Outcome.Required();

        var displayName = await applications.GetDisplayNameAsync(application, cancellationToken) ?? request.ClientId!;
        return new Outcome.Ask(Page(context, request, displayName, scopes, parameters));
    }

    /// <summary>
    /// Keeps the consent as a permanent authorization covering what the person consented to before and now, and audits
    /// it at the application's tenant.
    /// </summary>
    private async Task GrantAsync(HttpContext context, object application, string? client, ClaimsIdentity identity,
        EntraPerson person, IEnumerable<string> scopes)
    {
        var cancellationToken = context.RequestAborted;
        // What the person consented to before, then what's new. One authorization is the person's connection to the
        // application (#126): more scopes extend it.
        var granted = new List<string>();
        object? authorization = null;
        await foreach (var existing in authorizations.FindAsync(person.Subject, client, Statuses.Valid,
                           AuthorizationTypes.Permanent, scopes: null, cancellationToken))
        {
            authorization ??= existing;
            granted.AddRange((await authorizations.GetScopesAsync(existing, cancellationToken)).Where(s => !granted.Contains(s)));
        }
        granted.AddRange(scopes.Where(s => !granted.Contains(s)));

        // The consent itself. The tokens are issued under the session's own authorization (SignInSessions, #119), so
        // ending a session leaves the consent; revoking the connection revokes both (#126).
        if (authorization is null)
        {
            await authorizations.CreateAsync(identity, person.Subject, client!, AuthorizationTypes.Permanent,
                [.. granted], cancellationToken);
        }
        else
        {
            var descriptor = new OpenIddictAuthorizationDescriptor();
            await authorizations.PopulateAsync(descriptor, authorization, cancellationToken);
            descriptor.Scopes.UnionWith(granted);
            await authorizations.UpdateAsync(authorization, descriptor, cancellationToken);
        }

        var owner = ApiApplicationClient.Owner(await applications.GetPropertiesAsync(application, cancellationToken));
        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.ApiApplicationConsentGranted,
            TenantId = owner?.TenantId,
            TargetType = "api-application",
            TargetId = owner?.Id,
            ActorKind = AuditActorKind.Person,
            ActorSubject = person.Subject,
            ActorClientId = await applications.GetClientIdAsync(application, cancellationToken),
            ActorSuperadministrator = identity.HasClaim(Claims.Role, Superadministrator.Role),
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            Detail = $"scopes: {string.Join(' ', granted)}",
        }, cancellationToken);
    }

    /// <summary>
    /// The consent screen: what the application asks for, posted back to the authorization endpoint with the request's
    /// own parameters and an antiforgery token.
    /// </summary>
    private IResult Page(HttpContext context, OpenIddictRequest request, string displayName, IEnumerable<string> scopes,
        IEnumerable<KeyValuePair<string, StringValues>> parameters)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        // The answer returns to the application's redirect URI, which form-action must allow (CSP Level 3 §6.4.2).
        var redirect = request.RedirectUri is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            ? $" {parsed.GetLeftPart(UriPartial.Authority)}" : "";
        return AuthPages.Render<ConsentPage>(StatusCodes.Status200OK, new
        {
            Application = displayName,
            Scopes = scopes,
            Parameters = parameters,
            Antiforgery = tokens,
        }, formAction: $"'self'{redirect}");
    }
}
