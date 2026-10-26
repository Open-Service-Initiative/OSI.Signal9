namespace OSI.Signal9.Server.Data;

/// <summary>
/// A person's session with one client (#119): one sign-in through auth, from one Entra session, under the OpenIddict
/// authorization its code was issued with. Every token of the session belongs to that authorization, so revoking it
/// ends the session. auth re-checks it on every refresh and against Entra hourly (stories 27, 51-53, 59).
/// </summary>
public sealed class SignInSession
{
    /// <summary>The OpenIddict authorization the session's tokens belong to.</summary>
    public required string AuthorizationId { get; set; }

    /// <summary>Signal9's subject for the person (<c>{tid}:{oid}</c>).</summary>
    public required string Subject { get; set; }

    /// <summary>The client the session is with.</summary>
    public required string ClientId { get; set; }

    /// <summary>The person's Entra directory (<c>tid</c>), which scopes <see cref="EntraSessionId"/>.</summary>
    public Guid EntraTenantId { get; set; }

    /// <summary>The Entra session (<c>sid</c>) the sign-in came from, if Entra sent one.</summary>
    public string? EntraSessionId { get; set; }

    /// <summary>
    /// The person's account in auth's Entra token cache (MSAL's home account ID, <c>{uid}.{utid}</c>), whose refresh
    /// token the hourly Entra check uses.
    /// </summary>
    public required string EntraAccountId { get; set; }

    /// <summary>Whether Entra's most recent word was that the person is a Superadministrator (ADR-0006, story 59).</summary>
    public bool Superadministrator { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the person signed in at Entra for this session: an Entra session that ended later ends it.</summary>
    public DateTime SignedInAt { get; set; }

    /// <summary>When Entra last vouched for the person: their sign-in, then each forced refresh.</summary>
    public DateTime EntraCheckedAt { get; set; }
}

/// <summary>
/// An Entra session that ended (front-channel logout, story 51, or Entra refusing the person at the hourly check,
/// story 52): no sign-in from it made at or before <see cref="EndedAt"/> counts any more, neither auth's own sign-in
/// cookie nor a session's refresh token. Signing in at Entra afresh counts again.
/// </summary>
public sealed class EndedEntraSession
{
    public Guid EntraTenantId { get; set; }

    public required string EntraSessionId { get; set; }

    public DateTime EndedAt { get; set; }
}

/// <summary>
/// One entry of auth's Entra token cache (MSAL's serialized cache for one account), encrypted with Data Protection,
/// so every auth replica shares it and it survives restarts.
/// </summary>
public sealed class EntraTokenCacheEntry
{
    public required string Key { get; set; }

    public required byte[] Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}
