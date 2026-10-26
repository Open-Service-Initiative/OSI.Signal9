using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.Auth;

/// <summary>
/// Sign-ins at Entra that auth hasn't yet issued a code from or refused, kept in the database rather than in the sign-in
/// cookie (story 59): a cookie can be saved and replayed, so the cookie only names the record, and issuing or refusing
/// deletes it in one statement, which only one request can do.
/// </summary>
public sealed class FreshSignIns(Signal9DbContext db, TimeProvider time)
{
    /// <summary>
    /// How long a sign-in counts as fresh from Entra if no code is issued from it: long enough to answer the consent
    /// screen, short enough that app roles Entra since removed don't still count.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Records a sign-in that just came from Entra, answering re-authentication attempt <paramref name="reauthenticatedFor"/> if any.</summary>
    public async Task<Guid> RecordAsync(string? reauthenticatedFor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        // Abandoned sign-ins go when the next one comes.
        await db.FreshSignIns.Where(f => f.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var fresh = new FreshSignIn
        {
            Id = Guid.NewGuid(),
            ReauthenticatedFor = reauthenticatedFor,
            ExpiresAt = now + Lifetime,
        };
        db.FreshSignIns.Add(fresh);
        await db.SaveChangesAsync(cancellationToken);
        return fresh.Id;
    }

    /// <summary>The fresh sign-in the session's cookie names, unless it has been used or has expired.</summary>
    public async Task<FreshSignIn?> FindAsync(AuthenticationProperties session, CancellationToken cancellationToken)
    {
        if (!session.Items.TryGetValue(EntraSignIn.FreshSignInItem, out var value) || !Guid.TryParse(value, out var id))
            return null;
        var now = time.GetUtcNow().UtcDateTime;
        return await db.FreshSignIns.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id && f.ExpiresAt > now, cancellationToken);
    }

    /// <summary>Uses <paramref name="fresh"/> up; false when another request already did, or it has expired.</summary>
    public async Task<bool> ConsumeAsync(FreshSignIn? fresh, CancellationToken cancellationToken)
    {
        if (fresh is null)
            return false;
        var now = time.GetUtcNow().UtcDateTime;
        return await db.FreshSignIns.Where(f => f.Id == fresh.Id && f.ExpiresAt > now).ExecuteDeleteAsync(cancellationToken) == 1;
    }
}
