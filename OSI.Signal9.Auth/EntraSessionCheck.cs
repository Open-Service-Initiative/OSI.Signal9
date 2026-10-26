using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.EntityFrameworkCore.Models;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Auth;

/// <summary>What Entra said when auth refreshed a person's tokens there.</summary>
public abstract record EntraVerdict
{
    /// <summary>Entra still vouches for the person, with the ID token it just issued (its app roles are current).</summary>
    public sealed record Vouched(ClaimsPrincipal? IdToken) : EntraVerdict;

    /// <summary>Entra refused the person (<c>invalid_grant</c>, <c>interaction_required</c>): their sessions end.</summary>
    public sealed record Refused(string Error) : EntraVerdict;

    /// <summary>Entra couldn't be asked just now; the sessions go on and are checked again soon (story 50).</summary>
    public sealed record Unavailable : EntraVerdict;
}

/// <summary>
/// Forces a refresh of a person's tokens at Entra, bypassing every cached token (#119, story 52). It asks through MSAL,
/// with the registration's own credential and the refresh token in <see cref="EntraTokenCache"/>. Only Entra's
/// <c>invalid_grant</c> and <c>interaction_required</c> end the person's sessions (#63); MSAL reports both, and a cache
/// with no refresh token for the person, as <see cref="MsalUiRequiredException"/>: only the person signing in again can
/// fix any of them. Everything else, from network errors, Entra's 5xx and throttling to the outbound resilience pipeline
/// giving up, is transient.
/// </summary>
public sealed class EntraRefresh(ITokenAcquisition tokenAcquisition, ILogger<EntraRefresh> logger)
{
    /// <summary>
    /// What the refresh asks Entra for: only the sign-in's own OpenID Connect scopes, so it needs no API permission
    /// beyond signing in. Redeeming the refresh token is what Entra refuses for a disabled, deleted or revoked person,
    /// and the new ID token carries their current app roles.
    /// </summary>
    public static readonly string[] Scopes = ["openid", "profile", "offline_access"];

    public async Task<EntraVerdict> ForceAsync(string accountId, Guid entraTenantId, CancellationToken cancellationToken)
    {
        var parts = accountId.Split('.');
        if (parts.Length != 2)
            return new EntraVerdict.Refused("no_account");
        // MSAL finds the person's cached account by its home account ID.
        var account = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimConstants.UniqueObjectIdentifier, parts[0]),
            new Claim(ClaimConstants.UniqueTenantIdentifier, parts[1]),
        ], "entra-account"));

        try
        {
            // Named, so the directory isn't bound to the overload without a scheme as its tenant and user flow.
            var result = await tokenAcquisition.GetAuthenticationResultForUserAsync(Scopes,
                authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme, tenantId: entraTenantId.ToString(),
                user: account,
                tokenAcquisitionOptions: new TokenAcquisitionOptions
                {
                    // The sign-in's own registration, named again here: there's no request to infer it from.
                    AuthenticationOptionsName = OpenIdConnectDefaults.AuthenticationScheme,
                    ForceRefresh = true,
                    CancellationToken = cancellationToken,
                });
            return new EntraVerdict.Vouched(string.IsNullOrEmpty(result.IdToken)
                ? null
                : new ClaimsPrincipal(new ClaimsIdentity(new JsonWebToken(result.IdToken).Claims, "entra")));
        }
        catch (Exception exception) when (UiRequired(exception) is { } required)
        {
            return new EntraVerdict.Refused(required.ErrorCode);
        }
        // Anything else is transient, the outbound resilience pipeline giving up (its total timeout running out during
        // retries) included: the person is asked again at the next run, and the run goes on to everyone else.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Checking {Account} at Entra failed; their sessions go on.", accountId);
            return new EntraVerdict.Unavailable();
        }
    }

    private static MsalUiRequiredException? UiRequired(Exception exception) => exception switch
    {
        MsalUiRequiredException required => required,
        MicrosoftIdentityWebChallengeUserException { MsalUiRequiredException: { } required } => required,
        _ => null,
    };
}

/// <summary>
/// The hourly session check against Entra (#119, story 52): every live session's person is refreshed at Entra at least
/// once an hour, so disabling, deleting or revoking the sessions of a person in Entra ends their Signal9 sessions
/// within an hour. Each check also re-reads the Superadministrator app role (story 59). Every auth replica runs it;
/// each session is claimed by one.
/// </summary>
public sealed class EntraSessionCheck(IServiceScopeFactory scopes, TimeProvider time, ILogger<EntraSessionCheck> logger)
    : PeriodicJob(Interval, scopes, time, logger)
{
    /// <summary>How often due sessions are looked for.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>The longest Entra goes without being asked about a person with a live session.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<EntraSessionChecker>().RunAsync(cancellationToken);
}

/// <summary>One run of <see cref="EntraSessionCheck"/>.</summary>
public sealed class EntraSessionChecker(Signal9DbContext db, SignInSessions sessions, EntraRefresh entra,
    DeployingDirectory deploying, TimeProvider time, ILogger<EntraSessionChecker> logger)
{
    /// <summary>How long an ended Entra session is remembered: as long as any token from it could have lasted.</summary>
    private static readonly TimeSpan EndedRetention = AuthorizationServer.ApplicationRefreshTokenCap;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await PruneAsync(now, cancellationToken);

        // Due a run early, so the next run at the latest still asks within the hour. Claiming moves EntraCheckedAt on,
        // so another replica's run doesn't ask again.
        var due = now - (EntraSessionCheck.Every - EntraSessionCheck.Interval);
        var claimed = await db.SignInSessions
            .FromSql($"""UPDATE "SignInSessions" SET "EntraCheckedAt" = {now} WHERE "EntraCheckedAt" <= {due} RETURNING *""")
            .AsNoTracking().ToListAsync(cancellationToken);

        // One person's home account can sign in to several directories (a guest's home account isn't its sign-in
        // directory), and each directory's account is a different person to Entra and to Signal9 (ADR-0006). So each
        // directory is asked about its own sessions, and only its own answer counts for them.
        foreach (var account in claimed.GroupBy(s => (s.EntraAccountId, s.EntraTenantId)))
        {
            var accountSessions = account.ToList();
            switch (await entra.ForceAsync(account.Key.EntraAccountId, account.Key.EntraTenantId, cancellationToken))
            {
                case EntraVerdict.Refused refused:
                    await EndAsync(accountSessions, $"Entra refused the person: {Describe(refused.Error)}", cancellationToken);
                    break;
                case EntraVerdict.Vouched vouched:
                    // Entra must have answered for the sessions' own person (tid, oid); anyone else's answer vouches for
                    // no one here.
                    var answeredFor = vouched.IdToken is null ? null : EntraPerson.From(vouched.IdToken)?.Subject;
                    var someoneElse = vouched.IdToken is null ? [] : accountSessions.Where(s => s.Subject != answeredFor).ToList();
                    if (someoneElse.Count > 0)
                    {
                        logger.LogWarning("Entra's refresh for {Account} in {Directory} answered for someone else; their sessions end.",
                            account.Key.EntraAccountId, account.Key.EntraTenantId);
                        await EndAsync(someoneElse, "Entra answered for someone else", cancellationToken);
                    }
                    await RereadSuperadministratorAsync([.. accountSessions.Except(someoneElse)], vouched.IdToken, cancellationToken);
                    break;
                default:
                    // Asked again at the next run.
                    var ids = accountSessions.Select(s => s.AuthorizationId).ToList();
                    await db.SignInSessions.Where(s => ids.Contains(s.AuthorizationId))
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.EntraCheckedAt, due), cancellationToken);
                    break;
            }
        }
    }

    /// <summary>Ends the sessions; auth's own sign-in cookies from their Entra sessions no longer count either.</summary>
    private async Task EndAsync(List<SignInSession> ending, string detail, CancellationToken cancellationToken)
    {
        // Ended first, so a refresh racing this check finds no session rather than an ended Entra session, and the
        // audit entry gives this check's reason.
        await sessions.EndAsync(ending, new SessionEnd(detail), cancellationToken);
        foreach (var sid in ending.Select(s => (s.EntraTenantId, s.EntraSessionId)).Distinct())
            if (sid.EntraSessionId is { } id)
                await sessions.RecordEndedAsync(sid.EntraTenantId, id, cancellationToken);
    }

    /// <summary>Only Entra's own error codes reach the audit log; anything else is named generically.</summary>
    private static string Describe(string error) => error switch
    {
        "invalid_grant" or "interaction_required" => error,
        _ => "no usable Entra refresh token",
    };

    /// <summary>
    /// The Superadministrator app role as Entra sends it now (story 59). A session that lost it has its access tokens
    /// revoked, so the role leaves with the next request rather than when they expire; its next refresh issues tokens
    /// without it. An answer without an ID token can't confirm the role, so it's dropped. The ID token is for the
    /// sessions' own person, and the role counts only when that person is in the deploying directory (ADR-0006).
    /// </summary>
    private async Task RereadSuperadministratorAsync(List<SignInSession> accountSessions, ClaimsPrincipal? idToken,
        CancellationToken cancellationToken)
    {
        if (idToken is null)
            logger.LogWarning("Entra's refresh for {Account} returned no ID token; its Superadministrator role is dropped.",
                accountSessions[0].EntraAccountId);
        var superadministrator = idToken is not null && deploying.IsSuperadministrator(idToken);
        foreach (var session in accountSessions.Where(s => s.Superadministrator != superadministrator))
        {
            await db.SignInSessions.Where(s => s.AuthorizationId == session.AuthorizationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Superadministrator, superadministrator), cancellationToken);
            if (!superadministrator)
                await sessions.RevokeAccessTokensAsync(session, cancellationToken);
        }
    }

    /// <summary>
    /// Forgets what nothing needs any more: sessions whose authorization was revoked or pruned, ended Entra sessions
    /// no token can still come from, and cached Entra tokens of people with no session (such as refused sign-ins).
    /// </summary>
    private async Task PruneAsync(DateTime now, CancellationToken cancellationToken)
    {
        var authorizations = db.Set<OpenIddictEntityFrameworkCoreAuthorization>();
        await db.SignInSessions
            .Where(s => !authorizations.Any(a => a.Id == s.AuthorizationId && a.Status == Statuses.Valid))
            .ExecuteDeleteAsync(cancellationToken);
        var endedBefore = now - EndedRetention;
        await db.EndedEntraSessions.Where(e => e.EndedAt < endedBefore).ExecuteDeleteAsync(cancellationToken);
        // A sign-in still on its way to a session has written its cache entry moments ago.
        var idleSince = now - EntraSessionCheck.Every;
        await db.EntraTokenCache
            .Where(e => e.UpdatedAt < idleSince && !db.SignInSessions.Any(s => s.EntraAccountId == e.Key))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
