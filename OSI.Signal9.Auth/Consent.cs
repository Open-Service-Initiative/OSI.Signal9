using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
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
        /// <summary>Issue the code: the client needs no consent, or has it, with the authorization attached.</summary>
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
            await foreach (var existing in authorizations.FindAsync(person.Subject, client, Statuses.Valid,
                               AuthorizationTypes.Permanent, scopes, cancellationToken))
            {
                identity.SetAuthorizationId(await authorizations.GetIdAsync(existing, cancellationToken));
                return new Outcome.Granted();
            }
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
        // What the person consented to before, then what's new.
        var granted = new List<string>();
        await foreach (var existing in authorizations.FindAsync(person.Subject, client, Statuses.Valid,
                           AuthorizationTypes.Permanent, scopes: null, cancellationToken))
            granted.AddRange((await authorizations.GetScopesAsync(existing, cancellationToken)).Where(s => !granted.Contains(s)));
        granted.AddRange(scopes.Where(s => !granted.Contains(s)));

        var authorization = await authorizations.CreateAsync(identity, person.Subject, client!, AuthorizationTypes.Permanent,
            [.. granted], cancellationToken);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization, cancellationToken));

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
    /// own parameters and an antiforgery token. It can't be framed, cached or scripted.
    /// </summary>
    private IResult Page(HttpContext context, OpenIddictRequest request, string displayName, IEnumerable<string> scopes,
        IEnumerable<KeyValuePair<string, StringValues>> parameters)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        var html = HtmlEncoder.Default;
        var name = html.Encode(displayName);
        var page = new StringBuilder($"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Allow {name}? - Signal9</title></head>
            <body>
            <main>
            <h1>Allow {name} to act for you?</h1>
            <p>{name} is asking to use Signal9 as you. It can do only what you can do yourself, within the tenant it's registered at, and only the following:</p>
            <ul>

            """);
        foreach (var scope in scopes)
            page.Append($"<li><code>{html.Encode(scope)}</code>: {html.Encode(OSI.Signal9.Server.Identity.Scopes.All.GetValueOrDefault(scope, scope))}</li>\n");
        page.Append($"""
            </ul>
            <form method="post" action="/{AuthorizationServer.AuthorizationEndpoint}">

            """);
        foreach (var (key, values) in parameters)
            foreach (var value in values)
                page.Append($"<input type=\"hidden\" name=\"{html.Encode(key)}\" value=\"{html.Encode(value ?? "")}\">\n");
        page.Append($"""
            <input type="hidden" name="{html.Encode(tokens.FormFieldName)}" value="{html.Encode(tokens.RequestToken ?? "")}">
            <button type="submit" name="{DecisionField}" value="accept">Allow</button>
            <button type="submit" name="{DecisionField}" value="deny">Don't allow</button>
            </form>
            <p>Your answer is kept until you revoke it. You'll be asked again if {name} wants more.</p>
            </main>
            </body>
            </html>
            """);

        var headers = context.Response.Headers;
        // The answer returns to the application's redirect URI, which form-action must allow (CSP Level 3 §6.4.2).
        var redirect = request.RedirectUri is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            ? $" {parsed.GetLeftPart(UriPartial.Authority)}" : "";
        headers.ContentSecurityPolicy =
            $"default-src 'none'; form-action 'self'{redirect}; frame-ancestors 'none'; base-uri 'none'";
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        return Results.Content(page.ToString(), "text/html; charset=utf-8");
    }
}
