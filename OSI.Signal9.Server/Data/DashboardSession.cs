namespace OSI.Signal9.Server.Data;

/// <summary>
/// A person's session at <c>web</c>, the dashboard's backend-for-frontend (ADR-0003, story 46). The browser's cookie
/// holds only the session's ID, inside web's encrypted session ticket; the tokens web holds for the person live here,
/// encrypted with Data Protection, so every web replica sharing the database can use them (story 54).
/// </summary>
public sealed class DashboardSession
{
    public Guid Id { get; set; }

    /// <summary>Signal9's subject for the person.</summary>
    public required string Subject { get; set; }

    /// <summary>The access, refresh and identity tokens, protected with Data Protection for this session alone.</summary>
    public required byte[] ProtectedTokens { get; set; }

    /// <summary>When the access token expires.</summary>
    public DateTime AccessTokenExpiresAt { get; set; }

    /// <summary>When the person signed in: the session ends 12 hours later, however active (story 48).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the person last used the session, to the minute: it ends after an hour idle (story 48).</summary>
    public DateTime LastSeenAt { get; set; }
}
