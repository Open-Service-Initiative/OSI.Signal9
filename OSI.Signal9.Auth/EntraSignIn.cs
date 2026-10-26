using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.Auth;

/// <summary>
/// People sign in at <c>auth</c> through the operator's own multi-tenant Entra app registration (ADR-0002, stories
/// 38-44), with Microsoft.Identity.Web. Entra's code is redeemed by MSAL with the registration's credential: a federated
/// identity credential backed by managed identity on Azure, a certificate locally (stories 124, 125), never a secret.
/// A sign-in becomes a short session cookie at auth, which only the authorize endpoint reads; it creates no user
/// record (story 42).
/// </summary>
public static class EntraSignIn
{
    public const string SectionName = "Entra";

    /// <summary>Entra's public cloud, unless the operator's registration lives in another.</summary>
    public const string DefaultInstance = "https://login.microsoftonline.com/";

    /// <summary>The session at auth between Entra sign-in and the authorization response.</summary>
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string ChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>
    /// Set on the authorize request auth returns to when Entra sign-in was refused, so the authorize endpoint answers
    /// the client with <c>access_denied</c>. Its value is one of <see cref="Rejection"/>'s names, never free text.
    /// </summary>
    public const string RejectionParameter = "signal9_sign_in_rejected";

    /// <summary>
    /// Kept in the session at auth from Entra's sign-in until the authorization endpoint first issues from it, so it
    /// knows the session's app roles are as Entra just sent them.
    /// </summary>
    public const string FreshSignInItem = ".signal9.fresh";

    /// <summary>When the person signed in at Entra, which a sliding cookie's own issue time doesn't keep.</summary>
    public const string SignedInAtItem = ".signal9.signed_in_at";

    public static WebApplicationBuilder AddSignal9EntraSignIn(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(SectionName);
        ValidateRegistration(section);
        var allowedTenants = section.GetSection("AllowedTenantIds").Get<Guid[]>() ?? [];
        builder.Services.AddSingleton(new DeployingDirectory(section.GetValue<Guid>("DeployingTenantId")));

        builder.Services.AddAuthentication(CookieScheme)
            .AddMicrosoftIdentityWebApp(
                options =>
                {
                    section.Bind(options);
                    options.Instance ??= DefaultInstance;
                    // Work and school accounts from any directory: the registration is multi-tenant (story 39).
                    options.TenantId ??= "organizations";
                    // The authorization code flow with PKCE upstream too (RFC 9700 §2.1.1).
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.UsePkce = true;
                    // Entra's own claim names (tid, oid, amr, sid), not WS-Federation URIs.
                    options.MapInboundClaims = false;
                },
                cookies =>
                {
                    cookies.Cookie.Name = "__Host-signal9-sign-in";
                    cookies.Cookie.Path = "/";
                    cookies.Cookie.HttpOnly = true;
                    cookies.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    // Lax, so the cookie comes along when a client's site sends the browser to authorize.
                    cookies.Cookie.SameSite = SameSiteMode.Lax;
                    // An hour idle and 12 hours from the Entra sign-in in all (story 48): after that, only a fresh
                    // sign-in at Entra lets the person in, so its Conditional Access sign-in frequency applies (story 49).
                    cookies.ExpireTimeSpan = AuthorizationServer.DashboardSessionIdleTimeout;
                    cookies.SlidingExpiration = true;
                    cookies.Events.OnValidatePrincipal = context =>
                    {
                        if (SignedInAt(context.Properties) is not { } signedInAt
                            || context.Options.TimeProvider!.GetUtcNow() - signedInAt >= AuthorizationServer.DashboardSessionLifetime)
                        {
                            context.RejectPrincipal();
                            return context.HttpContext.SignOutAsync(CookieScheme);
                        }
                        return Task.CompletedTask;
                    };
                },
                openIdConnectScheme: ChallengeScheme,
                cookieScheme: CookieScheme)
            .EnableTokenAcquisitionToCallDownstreamApi();
        // Entra's tokens, the refresh token the hourly check uses included, in the database for every replica (#119).
        builder.Services.AddSingleton<IMsalTokenCacheProvider, EntraTokenCache>();

        builder.Services.Configure<OpenIdConnectOptions>(ChallengeScheme, options =>
        {
            options.MapInboundClaims = false;
            var tokenValidated = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                await tokenValidated(context);
                if (context.Result is not null)
                    return;
                if (Check(context.Principal!, allowedTenants, context.HttpContext) is { } rejection)
                {
                    await SignInAudit.FailedAsync(context.HttpContext, context.Principal, context.Properties, rejection);
                    if (ReturnRejected(context.HttpContext, context.Properties, rejection))
                        context.HandleResponse();
                    else
                        context.Fail(Describe(rejection));
                    return;
                }
                context.Properties!.Items[FreshSignInItem] = "true";
                context.Properties.Items[SignedInAtItem] = context.Options.TimeProvider!.GetUtcNow()
                    .ToString("O", CultureInfo.InvariantCulture);
                await SignInAudit.SucceededAsync(context.HttpContext, context.Principal!, context.Properties,
                    context.HttpContext.RequestServices.GetRequiredService<DeployingDirectory>().IsSuperadministrator(context.Principal!));
            };

            var remoteFailure = options.Events.OnRemoteFailure;
            options.Events.OnRemoteFailure = async context =>
            {
                await remoteFailure(context);
                await SignInAudit.FailedAsync(context.HttpContext, signIn: null, context.Properties, Rejection.Failed);
                if (context.Result is null && ReturnRejected(context.HttpContext, context.Properties, Rejection.Failed))
                    context.HandleResponse();
            };
        });

        return builder;
    }

    /// <summary>
    /// Sends the browser back to the authorize request it came from, which answers the client with
    /// <c>access_denied</c> (RFC 6749 §4.1.2.1). Without that request there's no client to tell.
    /// </summary>
    private static bool ReturnRejected(HttpContext context, AuthenticationProperties? properties, Rejection rejection)
    {
        if (properties?.RedirectUri is not { } returnUrl
            || !returnUrl.StartsWith(AuthorizationEndpoint.ReturnPath, StringComparison.Ordinal))
            return false;

        context.Response.Redirect(QueryHelpers.AddQueryString(returnUrl, RejectionParameter, rejection.ToString()));
        return true;
    }

    /// <summary>Why a sign-in at Entra didn't let the person in.</summary>
    public enum Rejection
    {
        /// <summary>Entra failed the sign-in, or it couldn't be completed.</summary>
        Failed,

        /// <summary>The sign-in didn't include MFA (stories 43, 44).</summary>
        NoMfa,

        /// <summary>The person's directory isn't on the operator's allow-list (story 40).</summary>
        UntrustedDirectory,

        /// <summary>The ID token doesn't name the directory and object (story 41).</summary>
        NoIdentity,

        /// <summary>
        /// The ID token doesn't say when the person authenticated (<c>auth_time</c>), so recent MFA can't be judged
        /// (story 45).
        /// </summary>
        NoAuthenticationTime,

        /// <summary>
        /// A sign-in auth asked Entra to make afresh (<c>prompt=login</c>) still authenticated the person too long ago for
        /// the client's <c>max_age</c>.
        /// </summary>
        NotFresh,
    }

    /// <summary>The OAuth error description the client gets for each <see cref="Rejection"/>.</summary>
    public static string Describe(Rejection rejection) => rejection switch
    {
        Rejection.NoMfa => "The sign-in didn't use multi-factor authentication.",
        Rejection.UntrustedDirectory => "The person's Entra directory isn't trusted by this deployment.",
        Rejection.NoIdentity => "The sign-in didn't identify the person's directory and object.",
        Rejection.NoAuthenticationTime => "The sign-in didn't say when the person authenticated.",
        Rejection.NotFresh => "Entra didn't authenticate the person again as required.",
        _ => "The sign-in at Entra didn't complete.",
    };

    private static Rejection? Check(ClaimsPrincipal principal, Guid[] allowedTenants, HttpContext context)
    {
        if (EntraPerson.From(principal) is not { } person)
            return Rejection.NoIdentity;

        // Entra writes "mfa" only when MFA was performed. Windows Integrated Authentication ("wia"), a certificate
        // alone ("rsa") and "wiaormfa" don't count (stories 43, 44).
        if (!principal.FindAll("amr").Any(claim => claim.Value == "mfa"))
            return Rejection.NoMfa;

        // When that MFA happened, which recent-MFA checks judge (story 45): an optional claim Entra must be set to send.
        if (AuthenticatedAt(principal) is null)
            return Rejection.NoAuthenticationTime;

        if (allowedTenants.Length > 0 && !allowedTenants.Contains(person.TenantId))
            return Rejection.UntrustedDirectory;

        // Front-channel logout finds sessions by sid (proof #1 checks Entra always sends it).
        if (principal.FindFirst("sid") is null)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EntraSignIn))
                .LogWarning("Entra sign-in for {Person} carried no sid.", person.Subject);
        }

        return null;
    }

    /// <summary>When the person signed in at Entra, as the sign-in's session at auth recorded it.</summary>
    public static DateTimeOffset? SignedInAt(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(SignedInAtItem, out var value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var signedInAt)
            ? signedInAt
            : null;

    /// <summary>
    /// The person's account in auth's Entra token cache: MSAL's home account ID, from Entra's <c>client_info</c>
    /// (<c>uid</c>, <c>utid</c>), which for a guest names their home directory rather than <c>tid</c>.
    /// </summary>
    public static string AccountId(ClaimsPrincipal signIn, EntraPerson person) =>
        signIn.FindFirst(ClaimConstants.UniqueObjectIdentifier)?.Value is { Length: > 0 } uid
        && signIn.FindFirst(ClaimConstants.UniqueTenantIdentifier)?.Value is { Length: > 0 } utid
            ? $"{uid}.{utid}"
            : $"{person.ObjectId:D}.{person.TenantId:D}";

    /// <summary>When the person last authenticated at Entra: the ID token's <c>auth_time</c> (OpenID Connect Core §2).</summary>
    public static DateTimeOffset? AuthenticatedAt(ClaimsPrincipal signIn) =>
        long.TryParse(signIn.FindFirst("auth_time")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>
    /// The registration must name its client and authenticate with a certificate or managed identity: no secrets
    /// (story 124).
    /// </summary>
    private static void ValidateRegistration(IConfigurationSection section)
    {
        if (string.IsNullOrWhiteSpace(section["ClientId"]))
            throw new InvalidOperationException($"{SectionName}:ClientId must name the operator's Entra app registration.");

        if (!Guid.TryParse(section["DeployingTenantId"], out var deploying) || deploying == Guid.Empty)
            throw new InvalidOperationException($"{SectionName}:DeployingTenantId must be the directory ID of the operator's own Entra tenant.");

        var credentials = section.GetSection("ClientCredentials").Get<CredentialDescription[]>() ?? [];
        if (credentials.Length == 0)
            throw new InvalidOperationException($"{SectionName}:ClientCredentials must give a certificate or a managed identity.");

        if (!string.IsNullOrEmpty(section["ClientSecret"])
            || credentials.Any(c => c.SourceType is not (CredentialSource.SignedAssertionFromManagedIdentity
                or CredentialSource.Certificate or CredentialSource.Path or CredentialSource.StoreWithThumbprint
                or CredentialSource.StoreWithDistinguishedName or CredentialSource.KeyVault
                or CredentialSource.Base64Encoded)))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ClientCredentials may only be a certificate or a managed identity's federated credential, never a client secret.");
        }
    }

}

/// <summary>
/// The operator's own Entra directory, which deployed Signal9 and holds its app registration. Only its people can be
/// Superadministrators: any consenting directory can assign the registration's app roles to its own people (story 58).
/// </summary>
public sealed record DeployingDirectory(Guid TenantId)
{
    /// <summary>
    /// Whether a sign-in at Entra makes the person a Superadministrator: it carries the app role, and comes from this
    /// directory.
    /// </summary>
    public bool IsSuperadministrator(ClaimsPrincipal signIn) =>
        EntraPerson.From(signIn)?.TenantId == TenantId
        && signIn.FindAll("roles").Any(claim => claim.Value == Superadministrator.AppRole);
}

/// <summary>
/// A person as Entra identifies them: their directory (<c>tid</c>) and their object in it (<c>oid</c>), never an email
/// or UPN, which can be renamed or recycled (story 41).
/// </summary>
public sealed record EntraPerson(Guid TenantId, Guid ObjectId)
{
    /// <summary>
    /// Signal9's subject for the person: stable, unique per (directory, object) and free of personal data.
    /// </summary>
    public string Subject => PersonSubject.Format(TenantId, ObjectId);

    public static EntraPerson? From(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst("tid")?.Value, out var tenant)
        && Guid.TryParse(principal.FindFirst("oid")?.Value, out var objectId)
        && tenant != Guid.Empty && objectId != Guid.Empty
            ? new EntraPerson(tenant, objectId)
            : null;
}
