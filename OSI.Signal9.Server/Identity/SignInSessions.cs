using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Server.Identity;

/// <summary>A live session and the IDs of every token issued under it, for tearing down what it holds open.</summary>
public sealed record LiveSession(SignInSession Session, IReadOnlyList<string> TokenIds);

/// <summary>Why a session is being revoked, and who asked, for its <see cref="AuditActions.SessionRevoked"/> entry.</summary>
/// <param name="Detail">A fixed description of the cause, never free text from a request.</param>
public sealed record SessionEnd(string Detail, AuditActorKind ActorKind = AuditActorKind.Anonymous,
    string? ActorSubject = null, string? ClientIp = null);

/// <summary>
/// People's sessions (#119), as auth starts them and every host checks and ends them. Entra stays the authority over
/// people (stories 27, 51-53, 59): a session ends when its Entra session does, when Entra refuses the person, and when
/// the person no longer holds a role assignment. Ending one revokes its authorization, and with it every token.
/// </summary>
public sealed class SignInSessions(
    Signal9DbContext db,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens,
    AuditLog audit,
    TimeProvider time,
    ILogger<SignInSessions> logger)
{
    /// <summary>
    /// Starts a session for the person in <paramref name="identity"/>: an authorization of its own, which the identity
    /// is then issued under, so its tokens can be revoked together without touching any consent.
    /// </summary>
    /// <param name="signedInAt">When the person signed in at Entra, the last time Entra vouched for them.</param>
    public async Task StartAsync(ClaimsIdentity identity, string applicationId, string clientId, Guid entraTenantId,
        string? entraSessionId, string entraAccountId, bool superadministrator, DateTimeOffset signedInAt,
        CancellationToken cancellationToken)
    {
        var subject = identity.GetClaim(Claims.Subject)!;
        var authorization = await authorizations.CreateAsync(new ClaimsPrincipal(identity), subject, applicationId,
            AuthorizationTypes.AdHoc, identity.GetScopes(), cancellationToken);
        var authorizationId = (await authorizations.GetIdAsync(authorization, cancellationToken))!;
        identity.SetAuthorizationId(authorizationId);

        var now = time.GetUtcNow().UtcDateTime;
        db.SignInSessions.Add(new SignInSession
        {
            AuthorizationId = authorizationId,
            Subject = subject,
            ClientId = clientId,
            EntraTenantId = entraTenantId,
            EntraSessionId = entraSessionId,
            EntraAccountId = entraAccountId,
            Superadministrator = superadministrator,
            CreatedAt = now,
            SignedInAt = signedInAt.UtcDateTime,
            EntraCheckedAt = signedInAt.UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Whether the Entra session ended at or after a sign-in from it at <paramref name="signedInAt"/>.</summary>
    public Task<bool> HasEndedAsync(Guid entraTenantId, string entraSessionId, DateTimeOffset signedInAt,
        CancellationToken cancellationToken)
    {
        var at = signedInAt.UtcDateTime;
        return db.EndedEntraSessions.AnyAsync(e => e.EntraTenantId == entraTenantId && e.EntraSessionId == entraSessionId
            && e.EndedAt >= at, cancellationToken);
    }

    /// <summary>What the check on a refresh found.</summary>
    /// <param name="Refusal">Why the refresh is refused (its session has then ended), or null when it may go ahead.</param>
    /// <param name="Superadministrator">Whether Entra last said the person is a Superadministrator.</param>
    public sealed record RefreshCheck(string? Refusal, bool Superadministrator);

    /// <summary>
    /// The check on every refresh (#63 "auth's checks on each refresh"): the session is still live, its Entra session
    /// hasn't ended, and the person still holds a role assignment, or Entra last said they're a Superadministrator. A
    /// session that fails is revoked with all its tokens.
    /// </summary>
    public async Task<RefreshCheck> CheckRefreshAsync(ClaimsPrincipal grant, CancellationToken cancellationToken)
    {
        // A grant from before sessions existed has none, and fails closed.
        if (grant.GetAuthorizationId() is not { } authorizationId
            || await db.SignInSessions.AsNoTracking().SingleOrDefaultAsync(s => s.AuthorizationId == authorizationId,
                cancellationToken) is not { } session)
            return new RefreshCheck("The session has ended.", false);

        if (session.EntraSessionId is { } sid
            && await HasEndedAsync(session.EntraTenantId, sid, new DateTimeOffset(session.SignedInAt, TimeSpan.Zero), cancellationToken))
        {
            await EndAsync([session], new SessionEnd("Entra session ended"), cancellationToken);
            return new RefreshCheck("The person signed out of Entra.", false);
        }

        if (!session.Superadministrator && !await HoldsRoleAssignmentAsync(session.Subject, cancellationToken))
        {
            await EndAsync([session], new SessionEnd("No role assignment left"), cancellationToken);
            return new RefreshCheck("The person no longer holds a role assignment.", false);
        }

        return new RefreshCheck(null, session.Superadministrator);
    }

    /// <summary>Whether the person has a user record holding at least one role assignment, at any tenant.</summary>
    public Task<bool> HoldsRoleAssignmentAsync(string subject, CancellationToken cancellationToken) =>
        PersonSubject.TryParse(subject, out var entraTenantId, out var entraObjectId)
            ? db.RoleAssignments.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.SessionCheck)
                .AnyAsync(a => a.User!.EntraTenantId == entraTenantId && a.User.EntraObjectId == entraObjectId, cancellationToken)
            : Task.FromResult(false);

    /// <summary>The person's live sessions, with every client, and their tokens.</summary>
    public Task<IReadOnlyList<LiveSession>> FindByUserAsync(string subject, CancellationToken cancellationToken) =>
        FindAsync(db.SignInSessions.Where(s => s.Subject == subject), cancellationToken);

    /// <summary>The live sessions from one Entra session (<c>sid</c> in directory <c>tid</c>), and their tokens.</summary>
    public Task<IReadOnlyList<LiveSession>> FindByEntraSessionAsync(Guid entraTenantId, string entraSessionId,
        CancellationToken cancellationToken) =>
        FindAsync(db.SignInSessions.Where(s => s.EntraTenantId == entraTenantId && s.EntraSessionId == entraSessionId),
            cancellationToken);

    private async Task<IReadOnlyList<LiveSession>> FindAsync(IQueryable<SignInSession> sessions,
        CancellationToken cancellationToken)
    {
        var live = await sessions.AsNoTracking()
            .Where(s => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
                .Any(a => a.Id == s.AuthorizationId && a.Status == Statuses.Valid))
            .ToListAsync(cancellationToken);
        var found = new List<LiveSession>();
        foreach (var session in live)
        {
            var ids = new List<string>();
            await foreach (var token in tokens.FindByAuthorizationIdAsync(session.AuthorizationId, cancellationToken))
                ids.Add((await tokens.GetIdAsync(token, cancellationToken))!);
            found.Add(new LiveSession(session, ids));
        }
        return found;
    }

    /// <summary>Ends every live session of the person, such as when their last role assignment goes (story 27).</summary>
    public async Task EndForPersonAsync(string subject, SessionEnd end, CancellationToken cancellationToken) =>
        await EndAsync([.. (await FindByUserAsync(subject, cancellationToken)).Select(s => s.Session)], end, cancellationToken);

    /// <summary>
    /// Ends an Entra session (front-channel logout, story 51): no session from it is issued tokens again, auth's own
    /// sign-in from it no longer counts, and its live sessions are revoked now.
    /// </summary>
    public async Task EndEntraSessionAsync(Guid entraTenantId, string entraSessionId, SessionEnd end,
        CancellationToken cancellationToken)
    {
        await RecordEndedAsync(entraTenantId, entraSessionId, cancellationToken);
        var live = await FindByEntraSessionAsync(entraTenantId, entraSessionId, cancellationToken);
        await EndAsync([.. live.Select(s => s.Session)], end, cancellationToken);
    }

    /// <summary>Records that an Entra session ended now: sign-ins from it until now no longer count.</summary>
    public async Task RecordEndedAsync(Guid entraTenantId, string entraSessionId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "EndedEntraSessions" ("EntraTenantId", "EntraSessionId", "EndedAt")
            VALUES ({entraTenantId}, {entraSessionId}, {now})
            ON CONFLICT ("EntraTenantId", "EntraSessionId") DO UPDATE SET "EndedAt" = excluded."EndedAt"
            """, cancellationToken);
    }

    /// <summary>
    /// Revokes each session's authorization, and so every token issued under it, and audits each at the person's owning
    /// tenant. Whichever caller removes a session audits it, once, when several end it at the same moment (the hourly
    /// check and a refresh, say).
    /// </summary>
    public async Task EndAsync(IReadOnlyCollection<SignInSession> sessions, SessionEnd end, CancellationToken cancellationToken)
    {
        foreach (var session in sessions)
        {
            var removed = await db.SignInSessions.Where(s => s.AuthorizationId == session.AuthorizationId)
                .ExecuteDeleteAsync(cancellationToken);
            if (await authorizations.FindByIdAsync(session.AuthorizationId, cancellationToken) is { } authorization
                && await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken))
            {
                await authorizations.TryRevokeAsync(authorization, cancellationToken);
            }
            if (removed == 0)
                continue;

            var person = await PersonAsync(session.Subject, cancellationToken);
            logger.LogInformation("Revoked session {Session} of {Person}: {Reason}.", session.AuthorizationId,
                session.Subject, end.Detail);
            await audit.WriteAsync(new AuditEntry
            {
                Action = AuditActions.SessionRevoked,
                TenantId = person?.TenantId,
                ActorKind = end.ActorKind,
                ActorSubject = end.ActorSubject,
                ActorClientId = session.ClientId,
                TargetType = "session",
                TargetId = Guid.TryParse(session.AuthorizationId, out var id) ? id : null,
                UserId = person?.Id,
                ClientIp = end.ClientIp,
                Detail = end.Detail,
            }, cancellationToken);
        }
    }

    /// <summary>Revokes the session's access tokens only, so its next request needs a refresh, which re-checks it.</summary>
    public async Task RevokeAccessTokensAsync(SignInSession session, CancellationToken cancellationToken)
    {
        await foreach (var token in tokens.FindByAuthorizationIdAsync(session.AuthorizationId, cancellationToken))
        {
            if (await tokens.HasTypeAsync(token, TokenTypeIdentifiers.AccessToken, cancellationToken)
                && await tokens.HasStatusAsync(token, Statuses.Valid, cancellationToken))
                await tokens.TryRevokeAsync(token, cancellationToken);
        }
    }

    private async Task<(Guid Id, Guid TenantId)?> PersonAsync(string subject, CancellationToken cancellationToken)
    {
        if (!PersonSubject.TryParse(subject, out var entraTenantId, out var entraObjectId))
            return null;
        var user = await db.Users.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.SessionCheck)
            .Where(u => u.EntraTenantId == entraTenantId && u.EntraObjectId == entraObjectId)
            .Select(u => new { u.Id, u.TenantId }).SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : (user.Id, user.TenantId);
    }
}
