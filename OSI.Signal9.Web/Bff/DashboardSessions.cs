using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// The tokens web holds for each signed-in person: a distributed, encrypted token cache in the database (story 46).
/// The tokens are protected with Data Protection, whose keys are in the database wrapped by the key-encryption key, for
/// one session each, so any web replica can use them (story 54) and a row can't be read as another session's.
/// </summary>
public sealed class DashboardSessions(Signal9DbContext db, IDataProtectionProvider dataProtection, TimeProvider time)
{
    /// <summary>The claim in web's session ticket that names the person's session.</summary>
    public const string SessionClaim = "signal9_session";

    private sealed record Tokens(string AccessToken, string? RefreshToken, string? IdToken);

    /// <summary>Starts a session for <paramref name="subject"/> holding a token response, and returns its ID.</summary>
    public async Task<Guid> StartAsync(string subject, OpenIdConnectMessage response, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var expiresIn = int.TryParse(response.ExpiresIn, out var seconds) ? seconds : 0;
        db.DashboardSessions.Add(new DashboardSession
        {
            Id = id,
            Subject = subject,
            ProtectedTokens = Protector(id).Protect(JsonSerializer.SerializeToUtf8Bytes(
                new Tokens(response.AccessToken, response.RefreshToken, response.IdToken))),
            AccessTokenExpiresAt = now.AddSeconds(expiresIn).UtcDateTime,
            CreatedAt = now.UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    /// <summary>Whether the session the person's ticket names still exists.</summary>
    public async Task<bool> ExistsAsync(ClaimsPrincipal person, CancellationToken cancellationToken) =>
        SessionId(person) is { } id && await db.DashboardSessions.AnyAsync(s => s.Id == id, cancellationToken);

    /// <summary>
    /// The access token web uses for the person, or null without a session or once it has expired. Refreshing it is
    /// #118's.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(ClaimsPrincipal person, CancellationToken cancellationToken)
    {
        if (SessionId(person) is not { } id
            || await db.DashboardSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } session
            || session.AccessTokenExpiresAt <= time.GetUtcNow().UtcDateTime)
            return null;

        return JsonSerializer.Deserialize<Tokens>(Protector(id).Unprotect(session.ProtectedTokens))!.AccessToken;
    }

    /// <summary>Ends the person's session: its tokens are gone, so a replayed ticket gets nothing.</summary>
    public async Task EndAsync(ClaimsPrincipal person, CancellationToken cancellationToken)
    {
        if (SessionId(person) is { } id)
            await db.DashboardSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private static Guid? SessionId(ClaimsPrincipal person) =>
        Guid.TryParse(person.FindFirst(SessionClaim)?.Value, out var id) ? id : null;

    private IDataProtector Protector(Guid session) =>
        dataProtection.CreateProtector("OSI.Signal9.Web.DashboardSessions.Tokens", session.ToString());
}
