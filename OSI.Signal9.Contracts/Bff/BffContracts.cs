namespace OSI.Signal9.Contracts.Bff;

/// <summary>
/// What the dashboard and the web host (its backend-for-frontend, ADR-0003) agree on. The dashboard never holds a
/// token: it calls web on its own origin with web's session cookie, and web attaches the access token on the server.
/// </summary>
public static class BffRoutes
{
    /// <summary>GET: the signed-in person (<see cref="BffUser"/>), or 401 without a session.</summary>
    public const string User = "bff/user";

    /// <summary>
    /// GET, as a full-page navigation: signs the person in, then returns to the local <c>returnUrl</c>. With
    /// <see cref="MaxAgeParameter"/>, the person must have authenticated at most that many seconds ago, such as after
    /// an RFC 9470 step-up challenge (#120).
    /// </summary>
    public const string Login = "bff/login";

    /// <summary>The <see cref="Login"/> query parameter that becomes OpenID Connect's <c>max_age</c>, in seconds.</summary>
    public const string MaxAgeParameter = "maxAge";

    /// <summary>POST, with the CSRF header: ends the session. Answers 204.</summary>
    public const string Logout = "bff/logout";

    /// <summary>
    /// Every state-changing request to web (anything but GET, HEAD, OPTIONS and TRACE) must carry this header with
    /// <see cref="CsrfHeaderValue"/>. A cross-site form can't set a custom header, and with no CORS anywhere a
    /// cross-site script can't either (RFC 10017 §6.1).
    /// </summary>
    public const string CsrfHeader = "X-CSRF";

    public const string CsrfHeaderValue = "1";
}

/// <summary>The signed-in person, as the dashboard shows them. Identity only: no token, no roles.</summary>
/// <param name="Subject">Signal9's subject for the person.</param>
/// <param name="Name">What to call them in the dashboard.</param>
public sealed record BffUser(string Subject, string Name);
