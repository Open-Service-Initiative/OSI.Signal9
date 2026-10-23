using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.Web.Bff;

/// <summary>Whether web can call api for a person, and with which access token.</summary>
public enum SessionTokenStatus
{
    /// <summary>The session has a current access token.</summary>
    Current,

    /// <summary>There's no session, or it has ended: the person must sign in again.</summary>
    Ended,

    /// <summary>The access token has expired and couldn't be refreshed just now; the session goes on (story 50).</summary>
    Unavailable,
}

/// <summary>An access token for the person's session, if there is one.</summary>
public readonly record struct SessionToken(SessionTokenStatus Status, string? AccessToken = null);

/// <summary>
/// The tokens web holds for each signed-in person: a distributed, encrypted token cache in the database (story 46).
/// The tokens are protected with Data Protection, whose keys are in the database wrapped by the key-encryption key, for
/// one session each, so any web replica can use them (story 54) and a row can't be read as another session's.
/// Sessions end after an hour idle or 12 hours in all (story 48); ending one revokes its refresh token at auth.
/// </summary>
public sealed class DashboardSessions(Signal9DbContext db, IDataProtectionProvider dataProtection, TimeProvider time,
    WebTokenClient tokenClient)
{
    /// <summary>The claim in web's session ticket that names the person's session.</summary>
    public const string SessionClaim = "signal9_session";

    /// <summary>An access token this close to expiring is refreshed before it's used.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    /// <summary>How stale <see cref="DashboardSession.LastSeenAt"/> may get before a request updates it.</summary>
    private static readonly TimeSpan ActivityResolution = TimeSpan.FromMinutes(1);

    private sealed record Tokens(string AccessToken, string? RefreshToken, string? IdToken);

    /// <summary>Starts a session for <paramref name="subject"/> holding a token response, and returns its ID.</summary>
    public async Task<Guid> StartAsync(string subject, OpenIdConnectMessage response, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var session = new DashboardSession
        {
            Id = id,
            Subject = subject,
            ProtectedTokens = [],
            CreatedAt = now.UtcDateTime,
            LastSeenAt = now.UtcDateTime,
        };
        Store(session, response, previous: null);
        db.DashboardSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    /// <summary>
    /// Whether the session the person's ticket names is still going, recording that it was just used. A session past
    /// its idle or absolute cap is ended here.
    /// </summary>
    public async Task<bool> ValidateAsync(ClaimsPrincipal person, CancellationToken cancellationToken)
    {
        if (SessionId(person) is not { } id
            || await db.DashboardSessions.AsNoTracking().Where(s => s.Id == id)
                .Select(s => new { s.CreatedAt, s.LastSeenAt }).SingleOrDefaultAsync(cancellationToken) is not { } session)
            return false;

        var now = time.GetUtcNow().UtcDateTime;
        if (IsOver(session.CreatedAt, session.LastSeenAt, now))
        {
            await EndAsync(id);
            return false;
        }

        if (now - session.LastSeenAt >= ActivityResolution)
            await db.DashboardSessions.Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now), cancellationToken);
        return true;
    }

    /// <summary>
    /// An access token for the person's session, refreshed first when it's about to expire, or when api just refused
    /// <paramref name="rejected"/>. Refreshes are serialized per session across replicas, so a burst of requests
    /// refreshes once. The session ends only when auth answers <c>invalid_grant</c> (story 50).
    /// </summary>
    public async Task<SessionToken> GetAccessTokenAsync(ClaimsPrincipal person, string? rejected,
        CancellationToken cancellationToken)
    {
        if (SessionId(person) is not { } id
            || await db.DashboardSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } session)
            return new SessionToken(SessionTokenStatus.Ended);

        if (Usable(session, rejected) is { } current)
            return new SessionToken(SessionTokenStatus.Current, current);

        // A refresh rotates the refresh token at auth, so it finishes even if the browser goes away: losing the new one
        // would sign the person out once the old one's reuse leeway passed.
        return await RefreshAsync(id, rejected);
    }

    private async Task<SessionToken> RefreshAsync(Guid id, string? rejected)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        // A retry after auth already rotated the token presents the old one again, which the reuse leeway allows.
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // The row lock serializes refreshes of this session across every replica sharing the database.
            var session = await db.DashboardSessions
                .FromSql($"""SELECT * FROM "DashboardSessions" WHERE "Id" = {id} FOR UPDATE""")
                .SingleOrDefaultAsync();
            if (session is null)
                return new SessionToken(SessionTokenStatus.Ended);

            // Another request refreshed it while this one waited.
            if (Usable(session, rejected) is { } current)
                return new SessionToken(SessionTokenStatus.Current, current);

            var tokens = Read(session);
            var refresh = tokens.RefreshToken is { } refreshToken
                ? await tokenClient.RefreshAsync(refreshToken, CancellationToken.None)
                : new TokenRefresh(null, Rejected: true);

            if (refresh.Rejected)
            {
                db.DashboardSessions.Remove(session);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return new SessionToken(SessionTokenStatus.Ended);
            }

            if (refresh.Tokens is not { } response)
            {
                // A transient failure: the current token serves while it lasts (a refresh ahead of time), and then
                // the person waits, still signed in.
                return rejected is null && session.AccessTokenExpiresAt > time.GetUtcNow().UtcDateTime
                    ? new SessionToken(SessionTokenStatus.Current, tokens.AccessToken)
                    : new SessionToken(SessionTokenStatus.Unavailable);
            }

            Store(session, response, tokens);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new SessionToken(SessionTokenStatus.Current, response.AccessToken);
        });
    }

    /// <summary>The session's access token, unless it needs refreshing.</summary>
    private string? Usable(DashboardSession session, string? rejected)
    {
        var accessToken = Read(session).AccessToken;
        return rejected is not null
            ? accessToken != rejected ? accessToken : null
            : session.AccessTokenExpiresAt - time.GetUtcNow().UtcDateTime > RefreshMargin ? accessToken : null;
    }

    /// <summary>Ends the person's session: its tokens are gone and its refresh token is revoked at auth.</summary>
    public async Task EndAsync(ClaimsPrincipal person)
    {
        if (SessionId(person) is { } id)
            await EndAsync(id);
    }

    /// <summary>Ends every session past its idle or absolute cap, such as those whose person never came back.</summary>
    public async Task EndExpiredAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var idleSince = now - AuthorizationServer.DashboardSessionIdleTimeout;
        var startedBefore = now - AuthorizationServer.DashboardSessionLifetime;
        var expired = await db.DashboardSessions.AsNoTracking()
            .Where(s => s.LastSeenAt <= idleSince || s.CreatedAt <= startedBefore)
            .Select(s => s.Id).ToListAsync(cancellationToken);
        foreach (var id in expired)
            await EndAsync(id);
    }

    /// <summary>
    /// Deletes the session and then revokes its refresh token, so whichever replica deletes it revokes it, once. Both
    /// run to the end whoever asked, so a session is never half-ended.
    /// </summary>
    private async Task EndAsync(Guid id)
    {
        var ended = await db.DashboardSessions
            .FromSql($"""DELETE FROM "DashboardSessions" WHERE "Id" = {id} RETURNING *""")
            .AsNoTracking().ToListAsync();
        foreach (var session in ended)
        {
            if (Read(session).RefreshToken is { } refreshToken)
                await tokenClient.RevokeAsync(refreshToken, CancellationToken.None);
        }
    }

    private static bool IsOver(DateTime createdAt, DateTime lastSeenAt, DateTime now) =>
        now - lastSeenAt >= AuthorizationServer.DashboardSessionIdleTimeout
        || now - createdAt >= AuthorizationServer.DashboardSessionLifetime;

    private void Store(DashboardSession session, OpenIdConnectMessage response, Tokens? previous)
    {
        var expiresIn = int.TryParse(response.ExpiresIn, out var seconds) ? seconds : 0;
        // A refresh response may leave out what didn't change (RFC 6749 §5.1, OpenID Connect Core §12.2).
        var tokens = new Tokens(response.AccessToken, response.RefreshToken ?? previous?.RefreshToken,
            response.IdToken ?? previous?.IdToken);
        session.ProtectedTokens = Protector(session.Id).Protect(JsonSerializer.SerializeToUtf8Bytes(tokens));
        session.AccessTokenExpiresAt = time.GetUtcNow().AddSeconds(expiresIn).UtcDateTime;
    }

    private Tokens Read(DashboardSession session) =>
        JsonSerializer.Deserialize<Tokens>(Protector(session.Id).Unprotect(session.ProtectedTokens))!;

    private static Guid? SessionId(ClaimsPrincipal person) =>
        Guid.TryParse(person.FindFirst(SessionClaim)?.Value, out var id) ? id : null;

    private IDataProtector Protector(Guid session) =>
        dataProtection.CreateProtector("OSI.Signal9.Web.DashboardSessions.Tokens", session.ToString());
}
